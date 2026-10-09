using System;
using System.Diagnostics;
using System.IO;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>Platform-specific, identity-checked MCP server termination.</summary>
    public class ProcessTerminator : IProcessTerminator
    {
        private readonly IProcessDetector _processDetector;

        public ProcessTerminator(IProcessDetector processDetector)
        {
            _processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
        }

        // Null means inspection failed; zero means the PID no longer exists. Never treat an
        // inspection error as permission to signal a process (or as proof of successful exit).
        protected virtual long? ReadStartTime(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.StartTime.ToUniversalTime().Ticks;
            }
            catch (ArgumentException) { return 0; }
            catch { return null; }
        }

        protected virtual bool IsWindows => Application.platform == RuntimePlatform.WindowsEditor;
        protected virtual void WaitForExitPoll() => System.Threading.Thread.Sleep(100);
        protected virtual long ElapsedMilliseconds(Stopwatch timer) => timer.ElapsedMilliseconds;
        protected virtual bool RunTerminationCommand(int pid, bool force)
        {
            if (IsWindows)
                return ExecPath.TryRun("taskkill", $"{(force ? "/F " : string.Empty)}/PID {pid} /T",
                    Application.dataPath, out _, out _);
            string killPath = File.Exists("/bin/kill") ? "/bin/kill" : "kill";
            return ExecPath.TryRun(killPath, $"-{(force ? 9 : 15)} {pid}",
                Application.dataPath, out _, out _);
        }

        /// <inheritdoc/>
        public bool Terminate(int pid)
        {
            // kill(0) and kill(-1) are group/broadcast operations; PID 1 is init/launchd.
            if (pid <= 1) return false;
            try
            {
                int currentPid = _processDetector.GetCurrentProcessId();
                if (currentPid <= 0 || pid == currentPid) return false;
                long? identity = ReadStartTime(pid);
                if (!identity.HasValue || identity.Value <= 0) return false;
                if (!_processDetector.LooksLikeMcpServerProcess(pid)) return false;

                // Ownership detection itself can take time. Recheck the same lifetime before
                // the first signal as well as escalation. A recycled PID is never a retry target.
                long? observed = ReadStartTime(pid);
                if (!observed.HasValue) return false;
                if (observed != identity) return true;
                bool graceful = RunTerminationCommand(pid, false);
                if (IsWindows && graceful) return true;

                var timer = Stopwatch.StartNew();
                while (!IsWindows && ElapsedMilliseconds(timer) < 8000)
                {
                    observed = ReadStartTime(pid);
                    if (!observed.HasValue) return false;
                    if (observed != identity) return true;
                    WaitForExitPoll();
                }

                observed = ReadStartTime(pid);
                if (!observed.HasValue) return false;
                if (observed != identity) return true;
                // PID-addressed commands still have a final check-to-signal race. Fully atomic
                // identity protection requires native handles/pidfds, unavailable on all targets.
                bool forced = RunTerminationCommand(pid, true);
                if (IsWindows) return forced;
                observed = ReadStartTime(pid);
                return observed.HasValue && observed != identity;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error killing process {pid}: {ex.Message}");
                return false;
            }
        }
    }
}
