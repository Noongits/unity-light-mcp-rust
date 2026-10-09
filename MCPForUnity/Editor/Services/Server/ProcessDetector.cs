using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Platform-specific process inspection for detecting MCP server processes.
    /// </summary>
    public class ProcessDetector : IProcessDetector
    {
        /// <inheritdoc/>
        public string NormalizeForMatch(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <inheritdoc/>
        public int GetCurrentProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        /// <inheritdoc/>
        public bool ProcessExists(int pid)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // On Windows, use tasklist to check if process exists
                    bool ok = ExecPath.TryRun("tasklist", $"/FI \"PID eq {pid}\"", Application.dataPath, out var stdout, out var stderr, 5000);
                    string combined = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).ToLowerInvariant();
                    return ok && combined.Contains(pid.ToString());
                }

                // Unix: ps exits non-zero when PID is not found.
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";
                ExecPath.TryRun(psPath, $"-p {pid} -o pid=", Application.dataPath, out var psStdout, out var psStderr, 2000);
                string combined2 = ((psStdout ?? string.Empty) + "\n" + (psStderr ?? string.Empty)).Trim();
                return !string.IsNullOrEmpty(combined2) && combined2.Any(char.IsDigit);
            }
            catch
            {
                return true; // Assume it exists if we cannot verify.
            }
        }

        /// <inheritdoc/>
        public virtual bool TryGetProcessCommandLine(int pid, out string argsLower)
        {
            argsLower = string.Empty;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Windows: use wmic to get command line
                    bool windowsOk = ExecPath.TryRun("cmd.exe", $"/c wmic process where \"ProcessId={pid}\" get CommandLine /value", Application.dataPath, out var wmicOut, out _, 5000);
                    return TryExtractWindowsCommandLine(windowsOk, wmicOut, out argsLower);
                }

                // Unix: ps -p pid -ww -o args=
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";

                bool ok = ExecPath.TryRun(psPath, $"-p {pid} -ww -o args=", Application.dataPath, out var stdout, out var stderr, 5000);
                if (!ok || string.IsNullOrWhiteSpace(stdout))
                {
                    return false;
                }
                string combined = (stdout ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(combined)) return false;
                // Normalize for matching to tolerate ps wrapping/newlines.
                argsLower = NormalizeForMatch(combined);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public List<int> GetListeningProcessIdsForPort(int port)
        {
            var results = new List<int>();
            try
            {
                string stdout, stderr;
                bool success;

                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Run netstat -ano directly (without findstr) and filter in C#.
                    // Using findstr in a pipe causes the entire command to return exit code 1 when no matches are found,
                    // which ExecPath.TryRun interprets as failure. Running netstat alone gives us exit code 0 on success.
                    success = ExecPath.TryRun("netstat.exe", "-ano", Application.dataPath, out stdout, out stderr);

                    // Process stdout regardless of success flag - netstat might still produce valid output
                    if (!string.IsNullOrEmpty(stdout))
                    {
                        string portSuffix = $":{port}";
                        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            // Windows netstat format: Proto  Local Address          Foreign Address        State           PID
                            // Example: TCP    0.0.0.0:8080           0.0.0.0:0              LISTENING       12345
                            if (line.Contains("LISTENING") && line.Contains(portSuffix))
                            {
                                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                // Verify the local address column actually ends with :{port}
                                // parts[0] = Proto (TCP), parts[1] = Local Address, parts[2] = Foreign Address, parts[3] = State, parts[4] = PID
                                if (parts.Length >= 5)
                                {
                                    string localAddr = parts[1];
                                    if (localAddr.EndsWith(portSuffix) && int.TryParse(parts[parts.Length - 1], out int parsedPid))
                                    {
                                        results.Add(parsedPid);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // lsof: only return LISTENers (avoids capturing random clients)
                    // Use /usr/sbin/lsof directly as it might not be in PATH for Unity
                    string lsofPath = "/usr/sbin/lsof";
                    if (!File.Exists(lsofPath)) lsofPath = "lsof"; // Fallback

                    // -nP: avoid DNS/service name lookups; faster and less error-prone
                    success = ExecPath.TryRun(lsofPath, $"-nP -iTCP:{port} -sTCP:LISTEN -t", Application.dataPath, out stdout, out stderr);
                    if (success && !string.IsNullOrWhiteSpace(stdout))
                    {
                        var pidStrings = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var pidString in pidStrings)
                        {
                            if (int.TryParse(pidString.Trim(), out int parsedPid))
                            {
                                results.Add(parsedPid);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Error checking port {port}: {ex.Message}");
            }
            return results.Distinct().ToList();
        }

        internal static bool TryExtractWindowsCommandLine(bool succeeded, string stdout, out string commandLine)
        {
            commandLine = string.Empty;
            if (!succeeded || string.IsNullOrWhiteSpace(stdout)) return false;
            foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = line.Trim();
                if (!value.StartsWith("CommandLine=", StringComparison.OrdinalIgnoreCase)) continue;
                commandLine = new ProcessDetector().NormalizeForMatch(value.Substring("CommandLine=".Length));
                return !string.IsNullOrEmpty(commandLine);
            }
            return false;
        }

        /// <inheritdoc/>
        public bool LooksLikeMcpServerProcess(int pid)
        {
            // This gates termination: a Python executable, HTTP transport or uvicorn alone
            // cannot establish ownership. Missing/failed metadata must fail closed.
            if (pid <= 1) return false;
            try
            {
                if (!TryGetProcessCommandLine(pid, out var commandLine)) return false;
                string command = NormalizeForMatch(commandLine);
                if (command.Contains("unityhub")) return false;
                return command.Contains("mcp-for-unity")
                    || command.Contains("mcp_for_unity")
                    || command.Contains("mcpforunity");
            }
            catch { return false; }
        }
    }
}
