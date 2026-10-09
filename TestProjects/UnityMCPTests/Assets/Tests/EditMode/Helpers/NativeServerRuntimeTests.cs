using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Helpers
{
    public class NativeServerRuntimeTests
    {
        string directory;
        [SetUp] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "native rust test " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
        [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        static string Hash(string value) { using(var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }

        [Test] public void WindowsPlatform_SelectsExeBundle() { Assert.AreEqual("windows-x86_64", NativeServerRuntime.GetPlatform(RuntimePlatform.WindowsEditor, Architecture.X64)); }
        [Test] public void MacPlatforms_SelectCorrectArchitecture()
        {
            Assert.AreEqual("macos-aarch64", NativeServerRuntime.GetPlatform(RuntimePlatform.OSXEditor, Architecture.Arm64));
            Assert.AreEqual("macos-x86_64", NativeServerRuntime.GetPlatform(RuntimePlatform.OSXEditor, Architecture.X64));
        }
        [Test] public void UnsupportedPlatform_FailsExplicitly() { Assert.Throws<PlatformNotSupportedException>(() => NativeServerRuntime.GetPlatform(RuntimePlatform.WindowsEditor, Architecture.X86)); }
        [Test] public void Copy_RepairsCorruptCacheAndDoesNotModifyPackage()
        {
            string source = Path.Combine(directory, "packaged.exe"); File.WriteAllText(source, "trusted executable");
            string cache = Path.Combine(directory, "cache with spaces");
            string target = NativeServerRuntime.Install(source, cache, "unity-mcp-light.exe", Hash("trusted executable"), true);
            File.WriteAllText(target, "corrupt cache");
            Assert.AreEqual(target, NativeServerRuntime.Install(source, cache, "unity-mcp-light.exe", Hash("trusted executable"), true));
            Assert.AreEqual("trusted executable", File.ReadAllText(target));
            Assert.AreEqual("trusted executable", File.ReadAllText(source));
            Assert.IsEmpty(Directory.GetFiles(cache, "*.tmp"));
        }
        [Test] public void InvalidPackage_IsRejectedEvenWithValidCache()
        {
            string source = Path.Combine(directory, "packaged.exe"); File.WriteAllText(source, "trusted executable");
            string cache = Path.Combine(directory, "cache");
            NativeServerRuntime.Install(source, cache, "unity-mcp-light.exe", Hash("trusted executable"), true);
            File.WriteAllText(source, "replaced executable");
            Assert.Throws<InvalidDataException>(() => NativeServerRuntime.Install(source, cache, "unity-mcp-light.exe", Hash("trusted executable"), true));
        }
        [Test] public void Copy_DoesNotReplaceOtherVersion()
        {
            string source = Path.Combine(directory, "packaged.exe"); File.WriteAllText(source, "one");
            string old = NativeServerRuntime.Install(source, Path.Combine(directory, Hash("one")), "unity-mcp-light.exe", Hash("one"), true);
            File.WriteAllText(source, "two");
            string current = NativeServerRuntime.Install(source, Path.Combine(directory, Hash("two")), "unity-mcp-light.exe", Hash("two"), true);
            Assert.AreNotEqual(old,current); Assert.AreEqual("one",File.ReadAllText(old)); Assert.AreEqual("two",File.ReadAllText(current));
        }
        [Test] public void WindowsArguments_QuoteSpacesQuotesAndTrailingBackslashes()
        {
            Assert.AreEqual("\"C:\\Program Files\\Rust\\\\\"", NativeServerRuntime.QuoteArgument("C:\\Program Files\\Rust\\"));
            Assert.AreEqual("\"a\\\"b\"",NativeServerRuntime.QuoteArgument("a\"b"));
        }
    }
}
