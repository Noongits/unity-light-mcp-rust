using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    public class ProcessOwnershipTimelineTests
    {
        private sealed class MetadataDetector : ProcessDetector, IProcessDetector
        {
            List<int> IProcessDetector.GetListeningProcessIdsForPort(int port) => new List<int> { 12345 };
            int IProcessDetector.GetCurrentProcessId() => 999;
            public bool Success = true;
            public string Metadata;
            public override bool TryGetProcessCommandLine(int pid, out string argsLower)
            {
                argsLower = NormalizeForMatch(Metadata);
                return Success;
            }
        }

        private sealed class Tracking : IPidFileManager
        {
            public string GetPidDirectory() => "";
            public string GetPidFilePath(int port) => "";
            public bool TryReadPid(string path, out int pid) { pid = 0; return false; }
            public bool TryGetPortFromPidFilePath(string path, out int port) { port = 0; return false; }
            public void DeletePidFile(string path) { }
            public void StoreHandshake(string path, string token) { }
            public bool TryGetHandshake(out string path, out string token) { path = token = null; return false; }
            public void StoreTracking(int pid, int port, string hash = null) { }
            public bool TryGetStoredPid(int port, out int pid) { pid = 12345; return true; }
            public string GetStoredArgsHash() => "";
            public void ClearTracking() { }
            public string ComputeShortHash(string input) => input;
        }

        private sealed class RecordingTerminator : IProcessTerminator
        {
            public int Calls;
            public bool Terminate(int pid) { Calls++; return true; }
        }

        [TestCase("python.exe unrelated.py")]
        [TestCase("uvx other-server --transport http")]
        [TestCase(null)]
        public void ReusedLegacyPidAndUnrelatedListener_StopNeverCallsTerminator(string metadata)
        {
            var terminator = new RecordingTerminator();
            var service = new ServerManagementService(new MetadataDetector { Metadata = metadata },
                new Tracking(), terminator);
            var stop = typeof(ServerManagementService).GetMethod("StopLocalHttpServerInternal",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsFalse((bool)stop.Invoke(service, new object[] { true, (int?)8080, true }));
            Assert.AreEqual(0, terminator.Calls);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("python.exe unrelated.py")]
        [TestCase("python.exe -m uvicorn unrelated:app")]
        [TestCase("uvx other-server --transport http")]
        public void UnrelatedOrMissingMetadata_NeverEstablishesOwnership(string command)
        {
            Assert.IsFalse(new MetadataDetector { Metadata = command }.LooksLikeMcpServerProcess(12345));
        }

        [Test]
        public void FailedMetadataQuery_EvenWithOldOutput_FailsClosed()
        {
            Assert.IsFalse(new MetadataDetector { Metadata = "python mcp-for-unity", Success = false }
                .LooksLikeMcpServerProcess(12345));
        }

        [TestCase("python -m mcp_for_unity --transport http")]
        [TestCase("uvx mcp-for-unity --transport http")]
        public void SuccessfulPackageMetadata_EstablishesOwnership(string command)
        {
            Assert.IsTrue(new MetadataDetector { Metadata = command }.LooksLikeMcpServerProcess(12345));
        }

        [TestCase(false, "CommandLine=python mcp-for-unity", false)]
        [TestCase(true, "Access denied", false)]
        [TestCase(true, "CommandLine=", false)]
        [TestCase(true, "CommandLine=python unrelated.py", true)]
        public void WindowsMetadata_RequiresSuccessfulNonemptyCommandLine(bool success, string stdout, bool expected)
        {
            Assert.AreEqual(expected, ProcessDetector.TryExtractWindowsCommandLine(success, stdout, out _));
        }
    }
}
