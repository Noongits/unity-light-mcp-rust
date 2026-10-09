using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>Installs the packaged Rust executable into a versioned, user-owned cache.</summary>
    public static class NativeServerRuntime
    {
        private static readonly object InstallLock = new object();

        public static string GetExecutableOrThrow()
        {
            if (!TryGetExecutable(out string executable, out string error))
                throw new InvalidOperationException(error);
            return executable;
        }

        public static bool TryGetExecutable(out string executable, out string error)
        {
            executable = null;
            error = null;
            try
            {
                string packageRoot = PackageInfo.FindForAssembly(typeof(NativeServerRuntime).Assembly)?.resolvedPath;
                if (string.IsNullOrEmpty(packageRoot))
                    packageRoot = Path.GetFullPath(AssetPathUtility.GetMcpPackageRootPath()
                        ?? throw new InvalidOperationException("Cannot locate the Unity MCP package."));
                string platform = GetPlatform(Application.platform, RuntimeInformation.ProcessArchitecture);
                string bundle = Path.Combine(packageRoot, "Server~");
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(bundle, "manifest.json")));
                var entry = manifest["binaries"]?[platform];
                if (entry == null)
                    throw new InvalidOperationException($"This package does not include a native Rust server for {platform}.");
                string file = (string)entry["file"];
                string hash = (string)entry["sha256"];
                if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file
                    || string.IsNullOrEmpty(hash) || !System.Text.RegularExpressions.Regex.IsMatch(hash, "\\A[0-9a-f]{64}\\z"))
                    throw new InvalidDataException("Invalid native server manifest.");
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityMCPRust", hash);
                executable = Install(Path.Combine(bundle, platform, file), cache, file, hash,
                    Application.platform == RuntimePlatform.WindowsEditor);
                return true;
            }
            catch (Exception ex)
            {
                executable = null;
                error = "Cannot prepare the native Rust server: " + ex.Message + " Update/reinstall the Unity MCP package from Noongits/unity-light-mcp-rust.";
                return false;
            }
        }

        internal static string GetPlatform(RuntimePlatform platform, Architecture architecture)
        {
            if (platform == RuntimePlatform.WindowsEditor && architecture == Architecture.X64)
                return "windows-x86_64";
            if (platform == RuntimePlatform.OSXEditor)
            {
                if (architecture == Architecture.Arm64) return "macos-aarch64";
                if (architecture == Architecture.X64) return "macos-x86_64";
            }
            if (platform == RuntimePlatform.LinuxEditor && architecture == Architecture.X64)
                return "linux-x86_64";
            throw new PlatformNotSupportedException($"Unsupported editor platform: {platform}/{architecture}");
        }

        internal static string Install(string source, string cache, string file, string expectedHash, bool windows)
        {
            lock (InstallLock)
            {
                // Validate even when cached: never launch a replaced/corrupt executable.
                if (!MatchesHash(source, expectedHash))
                    throw new InvalidDataException("The packaged Rust executable is missing or has an invalid SHA-256 checksum.");
                Directory.CreateDirectory(cache);
                string target = Path.Combine(cache, file);
                if (!MatchesHash(target, expectedHash))
                {
                    string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(source, temporary);
                        if (!MatchesHash(temporary, expectedHash))
                            throw new InvalidDataException("The copied Rust executable failed checksum verification.");
                        if (File.Exists(target)) File.Delete(target);
                        try { File.Move(temporary, target); }
                        catch (IOException) when (MatchesHash(target, expectedHash)) { }
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                if (!windows)
                {
                    using (var chmod = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/chmod", Arguments = "+x " + QuoteArgument(target),
                        UseShellExecute = false, CreateNoWindow = true
                    }))
                    {
                        if (chmod == null || !chmod.WaitForExit(5000) || chmod.ExitCode != 0)
                            throw new IOException("Could not make the cached Rust executable runnable.");
                    }
                }
                return target;
            }
        }

        internal static bool MatchesHash(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(), expected, StringComparison.Ordinal);
        }

        // ProcessStartInfo argument quoting (also preserves spaces and trailing backslashes on Windows).
        internal static string QuoteArgument(string value)
        {
            var result = new System.Text.StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value ?? string.Empty)
            {
                if (character == '\\') { backslashes++; continue; }
                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                result.Append(character);
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2);
            return result.Append('"').ToString();
        }
    }
}
