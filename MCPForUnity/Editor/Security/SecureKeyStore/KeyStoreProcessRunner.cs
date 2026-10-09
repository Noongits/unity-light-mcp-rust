using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Security
{
    // Owns only the process it starts. The timeout covers stdin, both pipes and exit.
    internal static class KeyStoreProcessRunner
    {
        internal interface IChild : IDisposable
        {
            Task<string> ReadOutputAsync();
            Task<string> ReadErrorAsync();
            Task WriteInputAsync(string input);
            bool WaitForExit(int timeoutMs);
            int ExitCode { get; }
            void Kill();
        }

        internal static (int code, string stdout, string stderr) Run(
            ProcessStartInfo info, string input = null, int timeoutMs = 5000,
            Func<ProcessStartInfo, IChild> start = null)
        {
            IChild child = null;
            bool completed = false;
            var elapsed = Stopwatch.StartNew();
            try
            {
                child = start != null ? start(info) : Start(info);
                if (child == null) return (-1, null, "Process did not start.");
                // Drain both pipes before waiting: either pipe can fill before process exit.
                Task<string> output = child.ReadOutputAsync();
                Task<string> error = child.ReadErrorAsync();
                Task writing = input == null ? Task.CompletedTask : child.WriteInputAsync(input);
                ObserveFailure(output);
                ObserveFailure(error);
                ObserveFailure(writing);
                if (!child.WaitForExit(Remaining(timeoutMs, elapsed)) ||
                    !Task.WaitAll(new Task[] { output, error, writing }, Remaining(timeoutMs, elapsed)))
                    return (-1, null, "Process timed out.");
                int code = child.ExitCode;
                completed = true;
                return (code, output.Result, error.Result);
            }
            catch
            {
                // Do not return command arguments or provider secrets in exception text.
                return (-1, null, "Process failed.");
            }
            finally
            {
                if (child != null)
                {
                    if (!completed)
                    {
                        try { child.Kill(); } catch { }
                        try { child.WaitForExit(250); } catch { }
                    }
                    try { child.Dispose(); } catch { }
                }
            }
        }

        private static int Remaining(int timeoutMs, Stopwatch elapsed) =>
            (int)Math.Max(0L, (long)timeoutMs - elapsed.ElapsedMilliseconds);

        private static void ObserveFailure(Task task) => task.ContinueWith(
            failed => { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

        private static IChild Start(ProcessStartInfo info)
        {
            var process = Process.Start(info);
            return process == null ? null : new Child(process);
        }

        private sealed class Child : IChild
        {
            private readonly Process _process;
            internal Child(Process process) { _process = process; }
            public Task<string> ReadOutputAsync() => _process.StandardOutput.ReadToEndAsync();
            public Task<string> ReadErrorAsync() => _process.StandardError.ReadToEndAsync();
            public async Task WriteInputAsync(string input)
            {
                try { await _process.StandardInput.WriteAsync(input).ConfigureAwait(false); }
                finally { _process.StandardInput.Close(); }
            }
            public bool WaitForExit(int timeoutMs) => _process.WaitForExit(timeoutMs);
            public int ExitCode => _process.ExitCode;
            public void Kill() { if (!_process.HasExited) _process.Kill(); }
            public void Dispose() => _process.Dispose();
        }
    }
}
