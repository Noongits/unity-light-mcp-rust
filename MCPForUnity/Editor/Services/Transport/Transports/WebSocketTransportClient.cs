using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Maintains a persistent WebSocket connection to the MCP server plugin hub.
    /// Handles registration, keep-alives, and command dispatch back into Unity via
    /// <see cref="TransportCommandDispatcher"/>.
    /// </summary>
    public class WebSocketTransportClient : IMcpTransportClient, IDisposable
    {
        private const string TransportDisplayName = "websocket";
        private static readonly TimeSpan[] ReconnectSchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };
        private static readonly TimeSpan ReconnectTailInterval = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

        private readonly IToolDiscoveryService _toolDiscoveryService;
        private ClientWebSocket _socket;
        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _connectionCts;
        private Task _receiveTask;
        private Task _keepAliveTask;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private int _lifecycleGeneration;
        private readonly object _stateLock = new();

        private Uri _endpointUri;
        private string _sessionId;
        private string _projectHash;
        private string _projectName;
        private string _projectPath;
        private string _unityVersion;
        private TimeSpan _keepAliveInterval = DefaultKeepAliveInterval;
        private TimeSpan _socketKeepAliveInterval = DefaultKeepAliveInterval;
        private volatile bool _isConnected;
        private int _isReconnectingFlag;
        private TransportState _state = TransportState.Disconnected(TransportDisplayName, "Transport not started");
        private string _apiKey;
        private bool _disposed;

        public WebSocketTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            _toolDiscoveryService = toolDiscoveryService;
        }

        public bool IsConnected => _isConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;

        private Task<List<ToolMetadata>> GetEnabledToolsOnMainThreadAsync(CancellationToken token)
        {
            return TransportCommandDispatcher.RunOnMainThreadAsync(
                () => _toolDiscoveryService?.GetEnabledTools() ?? new List<ToolMetadata>(),
                token);
        }

        public async Task<bool> StartAsync()
        {
            int generation;
            lock (_stateLock)
            {
                generation = Interlocked.Increment(ref _lifecycleGeneration);
                try { _lifecycleCts?.Cancel(); } catch (ObjectDisposedException) { }
            }
            await _lifecycleGate.WaitAsync();
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WebSocketTransportClient));
                if (generation != Volatile.Read(ref _lifecycleGeneration)) return false;
                try { return await StartCoreAsync(generation); }
                catch (OperationCanceledException)
                {
                    await StopCoreAsync();
                    return false;
                }
            }
            finally { _lifecycleGate.Release(); }
        }

        private async Task<bool> StartCoreAsync(int generation)
        {
            // Capture identity values on the main thread before any async context switching
            _projectName = ProjectIdentityUtility.GetProjectName();
            _projectHash = ProjectIdentityUtility.GetProjectHash();
            _unityVersion = Application.unityVersion;
            _apiKey = HttpEndpointUtility.IsRemoteScope()
                ? EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty)
                : string.Empty;

            if (HttpEndpointUtility.IsRemoteScope()
                && !HttpEndpointUtility.IsCurrentRemoteUrlAllowed(out string remoteUrlError))
            {
                string message = remoteUrlError ?? "HTTP Remote URL is not allowed by current security settings.";
                await StopCoreAsync();
                _state = TransportState.Disconnected(TransportDisplayName, message);
                McpLog.Error($"[WebSocket] {message}");
                return false;
            }

            // Get project root path (strip /Assets from dataPath) for focus nudging
            string dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string normalized = dataPath.TrimEnd('/', '\\');
                if (string.Equals(System.IO.Path.GetFileName(normalized), "Assets", StringComparison.Ordinal))
                {
                    _projectPath = System.IO.Path.GetDirectoryName(normalized) ?? normalized;
                }
                else
                {
                    _projectPath = normalized;  // Fallback if path doesn't end with Assets
                }
            }

            await StopCoreAsync();
            if (generation != Volatile.Read(ref _lifecycleGeneration)) return false;

            Uri endpoint = BuildWebSocketUri(HttpEndpointUtility.GetBaseUrl());
            CancellationToken token;
            lock (_stateLock)
            {
                if (generation != Volatile.Read(ref _lifecycleGeneration)) return false;
                _lifecycleCts = new CancellationTokenSource();
                _endpointUri = endpoint;
                _sessionId = null;
                token = _lifecycleCts.Token;
            }
            if (!await EstablishConnectionAsync(token))
            {
                await StopCoreAsync();
                return false;
            }

            if (generation == Volatile.Read(ref _lifecycleGeneration) && TryPublishConnected(token, false))
                return true;
            await StopCoreAsync();
            return false;
        }

        private bool TryPublishConnected(CancellationToken lifecycleToken, bool finishReconnect)
        {
            lock (_stateLock)
            {
                if (lifecycleToken.IsCancellationRequested || _connectionCts == null
                    || _connectionCts.IsCancellationRequested || _socket == null
                    || _socket.State != WebSocketState.Open) return false;
                _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId ?? "pending", details: _endpointUri.ToString());
                _isConnected = true;
                // Transfer ownership before releasing the lock. A close immediately
                // after publication must be able to schedule the next reconnect.
                if (finishReconnect) Interlocked.Exchange(ref _isReconnectingFlag, 0);
                return true;
            }
        }

        public async Task StopAsync()
        {
            int generation;
            // Invalidate and cancel atomically with lifecycle publication. Otherwise a
            // start can pass its generation check, then publish a CTS after cancellation
            // read null, leaving this stop waiting on an uncanceled connect.
            lock (_stateLock)
            {
                generation = Interlocked.Increment(ref _lifecycleGeneration);
                try { _lifecycleCts?.Cancel(); } catch (ObjectDisposedException) { }
            }
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Semaphore waiters are not an ordering contract. A newer Start owns
                // cleanup if it overtakes this queued Stop.
                if (generation == Volatile.Read(ref _lifecycleGeneration))
                    await StopCoreAsync().ConfigureAwait(false);
            }
            finally { _lifecycleGate.Release(); }
        }

        private async Task StopCoreAsync()
        {
            var lifecycle = _lifecycleCts;
            var socket = _socket;
            if (lifecycle == null)
            {
                return;
            }

            try
            {
                lifecycle.Cancel();
            }
            catch { }

            await StopConnectionLoopsAsync().ConfigureAwait(false);

            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", closeTimeout.Token).ConfigureAwait(false);
                    }
                }
                catch { }
                finally
                {
                    socket.Dispose();
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                }
            }

            lock (_stateLock)
            {
                if (ReferenceEquals(_lifecycleCts, lifecycle))
                {
                    _isConnected = false;
                    _state = TransportState.Disconnected(TransportDisplayName);
                    _lifecycleCts = null;
                    Interlocked.Exchange(ref _isReconnectingFlag, 0);
                }
            }
            lifecycle.Dispose();
        }

        /// <summary>
        /// Synchronous teardown for use in beforeAssemblyReload where async is not possible.
        /// Skips the graceful WebSocket close handshake and just disposes resources immediately.
        /// The server handles ungraceful disconnects via its ping timeout.
        /// </summary>
        public void ForceStop()
        {
            lock (_stateLock)
            {
                Interlocked.Increment(ref _lifecycleGeneration);
                try { _lifecycleCts?.Cancel(); } catch { }
                try { _connectionCts?.Cancel(); } catch { }

                if (_socket != null)
                {
                    try { _socket.Abort(); } catch { }
                    try { _socket.Dispose(); } catch { }
                    _socket = null;
                }

                try { _connectionCts?.Dispose(); } catch { }
                _connectionCts = null;
                _receiveTask = null;
                _keepAliveTask = null;
                Interlocked.Exchange(ref _isReconnectingFlag, 0);
                _isConnected = false;
                _state = TransportState.Disconnected(TransportDisplayName);

                try { _lifecycleCts?.Dispose(); } catch { }
                _lifecycleCts = null;
            }
        }


        public async Task<bool> VerifyAsync()
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                return false;
            }

            if (_lifecycleCts == null)
            {
                return false;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await SendPongAsync(timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Verify ping failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Disposing on Unity's main thread must not block a start continuation that
            // needs that same thread. Cancellation retires it; its finally releases gates.
            ForceStop();
            // Do not dispose semaphores while retired sends/lifecycle operations may
            // still be releasing them. They own no unmanaged resources unless queried
            // for AvailableWaitHandle, which this client never does.
        }

        private async Task<bool> EstablishConnectionAsync(CancellationToken token)
        {
            await StopConnectionLoopsAsync().ConfigureAwait(false);

            CancellationToken connectionToken;
            lock (_stateLock)
            {
                token.ThrowIfCancellationRequested();
                _connectionCts?.Dispose();
                _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _sessionId = null;
                connectionToken = _connectionCts.Token;
            }

            Uri originalEndpoint = _endpointUri;
            Uri connectedEndpoint = null;
            Exception lastConnectError = null;

            foreach (Uri candidate in BuildConnectionCandidateUris(originalEndpoint))
            {
                connectionToken.ThrowIfCancellationRequested();

                ClientWebSocket socket;
                lock (_stateLock)
                {
                    connectionToken.ThrowIfCancellationRequested();
                    _socket?.Dispose();
                    socket = _socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = _socketKeepAliveInterval;
                    if (!string.IsNullOrEmpty(_apiKey))
                        socket.Options.SetRequestHeader(AuthConstants.ApiKeyHeader, _apiKey);
                }

                try
                {
                    await socket.ConnectAsync(candidate, connectionToken).ConfigureAwait(false);
                    connectedEndpoint = candidate;
                    break;
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastConnectError = ex;
                    McpLog.Debug($"[WebSocket] Connect failed for {candidate}: {ex.Message}");
                }
            }

            if (connectedEndpoint == null)
            {
                string errorMsg = "Connection failed. Check that the server URL is correct, the server is running, and your API key (if required) is valid.";
                McpLog.Error($"[WebSocket] {errorMsg} (Detail: {lastConnectError?.Message ?? "Unknown error"})");
                lock (_stateLock)
                {
                    token.ThrowIfCancellationRequested();
                    _state = TransportState.Disconnected(TransportDisplayName, errorMsg);
                }
                return false;
            }

            lock (_stateLock)
            {
                connectionToken.ThrowIfCancellationRequested();
                if (!string.Equals(connectedEndpoint.Host, originalEndpoint.Host, StringComparison.OrdinalIgnoreCase))
                {
                    McpLog.Warn($"[WebSocket] Connected via fallback host '{connectedEndpoint.Host}' after '{originalEndpoint.Host}' failed.");
                    _endpointUri = connectedEndpoint;
                }
                StartBackgroundLoops(connectionToken);
            }

            try
            {
                await SendRegisterAsync(connectionToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string regMsg = $"Registration with server failed: {ex.Message}";
                McpLog.Error($"[WebSocket] {regMsg}");
                lock (_stateLock)
                {
                    token.ThrowIfCancellationRequested();
                    _state = TransportState.Disconnected(TransportDisplayName, regMsg);
                }
                return false;
            }

            return !connectionToken.IsCancellationRequested;
        }

        /// <summary>
        /// Stops the connection loops and disposes of the connection CTS.
        /// Particularly useful when reconnecting, we want to ensure that background loops are cancelled correctly before starting new oens
        /// </summary>
        /// <param name="awaitTasks">Whether to await the receive and keep alive tasks before disposing.</param>
        private async Task StopConnectionLoopsAsync(bool awaitTasks = true)
        {
            // Retire this connection's resources before yielding. A force-stop or a
            // concurrent closure must never let this continuation clear a newer task/CTS.
            CancellationTokenSource connection;
            Task receive, keepAlive;
            lock (_stateLock)
            {
                connection = _connectionCts;
                receive = _receiveTask;
                keepAlive = _keepAliveTask;
                _connectionCts = null;
                if (awaitTasks)
                {
                    _receiveTask = null;
                    _keepAliveTask = null;
                }
            }
            try { connection?.Cancel(); } catch (ObjectDisposedException) { }
            if (awaitTasks)
            {
                if (receive != null) { try { await receive.ConfigureAwait(false); } catch { } }
                if (keepAlive != null) { try { await keepAlive.ConfigureAwait(false); } catch { } }
            }
            connection?.Dispose();
        }

        private void StartBackgroundLoops(CancellationToken token)
        {
            if ((_receiveTask != null && !_receiveTask.IsCompleted) || (_keepAliveTask != null && !_keepAliveTask.IsCompleted))
            {
                return;
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string message = await ReceiveMessageAsync(token).ConfigureAwait(false);
                    if (message == null)
                    {
                        continue;
                    }
                    await HandleMessageAsync(message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException wse)
                {
                    McpLog.Warn($"[WebSocket] Receive loop error: {wse.Message}");
                    await HandleConnectionClosureAsync(wse.Message, token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[WebSocket] Unexpected receive error: {ex.Message}");
                    await HandleConnectionClosureAsync(ex.Message, token).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken token)
        {
            var socket = _socket;
            if (socket == null) return null;

            byte[] rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            var buffer = new ArraySegment<byte>(rentedBuffer);
            using var ms = new MemoryStream(8192);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleConnectionClosureAsync(result.CloseStatusDescription ?? "Server closed connection", token).ConfigureAwait(false);
                        return null;
                    }

                    if (result.Count > 0)
                    {
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                if (ms.Length == 0)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private async Task HandleMessageAsync(string message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            JObject payload;
            try
            {
                payload = JObject.Parse(message);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Invalid JSON payload: {ex.Message}");
                return;
            }

            string messageType = payload.Value<string>("type") ?? string.Empty;

            switch (messageType)
            {
                case "welcome":
                    lock (_stateLock)
                    {
                        token.ThrowIfCancellationRequested();
                        ApplyWelcome(payload);
                    }
                    break;
                case "registered":
                    await HandleRegisteredAsync(payload, token).ConfigureAwait(false);
                    break;
                case "execute":
                    await HandleExecuteAsync(payload, token).ConfigureAwait(false);
                    break;
                case "ping":
                    await SendPongAsync(token).ConfigureAwait(false);
                    break;
                default:
                    // No-op for unrecognised types (keep-alives, telemetry, etc.)
                    break;
            }
        }

        private void ApplyWelcome(JObject payload)
        {
            int? keepAliveSeconds = payload.Value<int?>("keepAliveInterval");
            if (keepAliveSeconds.HasValue && keepAliveSeconds.Value > 0)
            {
                _keepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds.Value);
                _socketKeepAliveInterval = _keepAliveInterval;
            }

            int? serverTimeoutSeconds = payload.Value<int?>("serverTimeout");
            if (serverTimeoutSeconds.HasValue)
            {
                int sourceSeconds = keepAliveSeconds ?? serverTimeoutSeconds.Value;
                int safeSeconds = Math.Max(5, Math.Min(serverTimeoutSeconds.Value, sourceSeconds));
                _socketKeepAliveInterval = TimeSpan.FromSeconds(safeSeconds);
            }
        }

        private async Task HandleRegisteredAsync(JObject payload, CancellationToken token)
        {
            string newSessionId = payload.Value<string>("session_id");
            if (!string.IsNullOrEmpty(newSessionId))
            {
                lock (_stateLock)
                {
                    token.ThrowIfCancellationRequested();
                    _sessionId = newSessionId;
                    ProjectIdentityUtility.SetSessionId(_sessionId);
                    _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                }
                McpLog.Info($"[WebSocket] Registered with session ID: {newSessionId}", false);

                await SendRegisterToolsAsync(token).ConfigureAwait(false);
            }
        }

        private async Task SendRegisterToolsAsync(CancellationToken token)
        {
            if (_toolDiscoveryService == null) return;

            token.ThrowIfCancellationRequested();
            var tools = await GetEnabledToolsOnMainThreadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            McpLog.Info($"[WebSocket] Preparing to register {tools.Count} tool(s) with the bridge.", false);
            var toolsArray = new JArray();

            foreach (var tool in tools)
            {
                var toolObj = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["structured_output"] = tool.StructuredOutput,
                    ["requires_polling"] = tool.RequiresPolling,
                    ["poll_action"] = tool.PollAction ?? "status",
                    ["max_poll_seconds"] = tool.MaxPollSeconds,
                    ["group"] = string.IsNullOrWhiteSpace(tool.Group) ? "core" : tool.Group
                };

                var paramsArray = new JArray();
                if (tool.Parameters != null)
                {
                    foreach (var p in tool.Parameters)
                    {
                        paramsArray.Add(new JObject
                        {
                            ["name"] = p.Name,
                            ["description"] = p.Description,
                            ["type"] = p.Type,
                            ["required"] = p.Required,
                            ["default_value"] = p.DefaultValue
                        });
                    }
                }
                toolObj["parameters"] = paramsArray;
                toolsArray.Add(toolObj);
            }

            var payload = new JObject
            {
                ["type"] = "register_tools",
                ["tools"] = toolsArray
            };

            await SendJsonAsync(payload, token).ConfigureAwait(false);
            McpLog.Info($"[WebSocket] Sent {tools.Count} tools registration", false);
        }

        public async Task ReregisterToolsAsync()
        {
            if (!IsConnected || _lifecycleCts == null)
            {
                McpLog.Warn("[WebSocket] Cannot reregister tools: not connected");
                return;
            }

            try
            {
                await SendRegisterToolsAsync(_lifecycleCts.Token).ConfigureAwait(false);
                McpLog.Info("[WebSocket] Tool reregistration completed", false);
            }
            catch (System.OperationCanceledException)
            {
                McpLog.Warn("[WebSocket] Tool reregistration cancelled");
            }
            catch (System.Exception ex)
            {
                McpLog.Error($"[WebSocket] Tool reregistration failed: {ex.Message}");
            }
        }

        private async Task HandleExecuteAsync(JObject payload, CancellationToken token)
        {
            string commandId = payload.Value<string>("id");
            string commandName = payload.Value<string>("name");
            JObject parameters = payload.Value<JObject>("params") ?? new JObject();
            int timeoutSeconds = payload.Value<int?>("timeout") ?? (int)DefaultCommandTimeout.TotalSeconds;

            if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(commandName))
            {
                McpLog.Warn("[WebSocket] Invalid execute payload (missing id or name)");
                return;
            }

            var commandEnvelope = new JObject
            {
                ["type"] = commandName,
                ["params"] = parameters
            };

            string responseJson;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
                responseJson = await TransportCommandDispatcher.ExecuteCommandJsonAsync(commandEnvelope.ToString(Formatting.None), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"Command '{commandName}' timed out after {timeoutSeconds} seconds"
                });
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message
                });
            }

            JToken resultToken;
            try
            {
                resultToken = JToken.Parse(responseJson);
            }
            catch
            {
                resultToken = new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Invalid response payload"
                };
            }

            var responsePayload = new JObject
            {
                ["type"] = "command_result",
                ["id"] = commandId,
                ["result"] = resultToken
            };

            await SendJsonAsync(responsePayload, token).ConfigureAwait(false);
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_keepAliveInterval, token).ConfigureAwait(false);
                    if (_socket == null || _socket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    await SendPongAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[WebSocket] Keep-alive failed: {ex.Message}");
                    await HandleConnectionClosureAsync(ex.Message, token).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task SendRegisterAsync(CancellationToken token)
        {
            var registerPayload = new JObject
            {
                ["type"] = "register",
                // session_id is now server-authoritative; omitted here or sent as null
                ["project_name"] = _projectName,
                ["project_hash"] = _projectHash,
                ["unity_version"] = _unityVersion,
                ["project_path"] = _projectPath
            };

            await SendJsonAsync(registerPayload, token).ConfigureAwait(false);
        }

        private Task SendPongAsync(CancellationToken token)
        {
            var payload = new JObject
            {
                ["type"] = "pong",
                ["session_id"] = _sessionId  // Include session ID for server-side tracking
            };
            return SendJsonAsync(payload, token);
        }

        private async Task SendJsonAsync(JObject payload, CancellationToken token)
        {
            var socket = _socket;
            if (socket == null)
            {
                throw new InvalidOperationException("WebSocket is not initialised");
            }

            string json = payload.ToString(Formatting.None);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var buffer = new ArraySegment<byte>(bytes);

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("WebSocket is not open");
                }

                await socket.SendAsync(buffer, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private Task HandleConnectionClosureAsync(string reason, CancellationToken connectionToken)
        {
            lock (_stateLock)
            {
                return connectionToken.IsCancellationRequested
                    ? Task.CompletedTask
                    : HandleSocketClosureAsync(reason);
            }
        }

        private async Task HandleSocketClosureAsync(string reason)
        {
            lock (_stateLock)
            {
                // Capture stack trace for debugging disconnection triggers
                var stackTrace = new System.Diagnostics.StackTrace(true);
                McpLog.Debug($"[WebSocket] HandleSocketClosureAsync called. Reason: {reason}\nStack trace:\n{stackTrace}");

                // Capture this lifecycle before teardown or queued work. StopAsync/ForceStop
                // can dispose and clear the field before a Task.Run callback gets a worker.
                var lifecycle = _lifecycleCts;
                if (lifecycle == null || lifecycle.IsCancellationRequested)
                {
                    return;
                }

                CancellationToken lifecycleToken;
                try { lifecycleToken = lifecycle.Token; }
                catch (ObjectDisposedException) { return; }

                bool scheduleReconnect = Interlocked.CompareExchange(ref _isReconnectingFlag, 1, 0) == 0;

                _isConnected = false;
                _state = TransportState.Disconnected(TransportDisplayName, reason ?? "Connection closed");
                McpLog.Warn($"[WebSocket] Connection closed: {reason}");

                // Non-waiting teardown completes synchronously and only retires this connection.
                StopConnectionLoopsAsync(awaitTasks: false).GetAwaiter().GetResult();

                // Even when a reconnect already owns the gate, retire this failed
                // candidate so that attempt cannot publish it as connected.
                if (scheduleReconnect) _ = ScheduleReconnectAsync(lifecycleToken);
            }
            await Task.CompletedTask;
        }


        private Task ScheduleReconnectAsync(CancellationToken token)
            => Task.Run(() => AttemptReconnectAsync(token), CancellationToken.None);

        private async Task AttemptReconnectAsync(CancellationToken token)
        {
            bool entered = false;
            bool published = false;
            try
            {
                await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                // A queued reconnect can outlive the lifecycle that scheduled it. Do not
                // touch shared connection tasks when that lifecycle has already stopped.
                if (token.IsCancellationRequested) return;
                await StopConnectionLoopsAsync().ConfigureAwait(false);

                foreach (TimeSpan delay in ReconnectSchedule)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }

                    if (await EstablishConnectionAsync(token).ConfigureAwait(false)
                        && TryPublishConnected(token, true))
                    {
                        published = true;
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        return;
                    }
                }

                // Schedule exhausted — keep retrying every 30 s indefinitely so a transient
                // server outage longer than ~49 s doesn't leave the plugin permanently dead.
                McpLog.Warn($"[WebSocket] Initial reconnect schedule exhausted. Retrying every {ReconnectTailInterval.TotalSeconds}s until cancelled.");
                lock (_stateLock)
                {
                    token.ThrowIfCancellationRequested();
                    _state = _state.WithError($"Server unreachable – retrying every {ReconnectTailInterval.TotalSeconds} s");
                }
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(ReconnectTailInterval, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    if (await EstablishConnectionAsync(token).ConfigureAwait(false)
                        && TryPublishConnected(token, true))
                    {
                        published = true;
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                // A canceled reconnect can finish after ForceStop has allowed a new one.
                lock (_stateLock)
                {
                    var lifecycle = _lifecycleCts;
                    try
                    {
                        if (!published && lifecycle != null && lifecycle.Token == token)
                            Interlocked.Exchange(ref _isReconnectingFlag, 0);
                    }
                    catch (ObjectDisposedException) { }
                }
                if (entered) _lifecycleGate.Release();
            }
        }

        private static Uri BuildWebSocketUri(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var httpUri))
            {
                throw new InvalidOperationException($"Invalid MCP base URL: {baseUrl}");
            }

            // Replace bind-only addresses for client connections
            // 0.0.0.0 and :: are only valid for server binding, not client connections
            string host = httpUri.Host;
            if (host == "0.0.0.0")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '127.0.0.1' for client connection.");
                host = "127.0.0.1";
            }
            else if (host == "::")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '::1' for client connection.");
                host = "::1";
            }

            var builder = new UriBuilder(httpUri)
            {
                Scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Host = host,
                Path = httpUri.AbsolutePath.TrimEnd('/') + "/hub/plugin"
            };

            return builder.Uri;
        }

        private static List<Uri> BuildConnectionCandidateUris(Uri endpointUri)
        {
            var candidates = new List<Uri>();
            if (endpointUri == null)
            {
                return candidates;
            }

            candidates.Add(endpointUri);

            if (!string.Equals(endpointUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            // Retry localhost using explicit loopback hosts to avoid DNS family ambiguity on some machines.
            TryAddCandidate(candidates, endpointUri, "127.0.0.1");
            TryAddCandidate(candidates, endpointUri, "::1");
            return candidates;
        }

        private static void TryAddCandidate(List<Uri> candidates, Uri template, string host)
        {
            try
            {
                var builder = new UriBuilder(template) { Host = host };
                Uri candidate = builder.Uri;
                foreach (Uri existing in candidates)
                {
                    if (Uri.Compare(existing, candidate, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        return;
                    }
                }
                candidates.Add(candidate);
            }
            catch
            {
                // Ignore malformed fallback candidate and continue with remaining options.
            }
        }
    }
}
