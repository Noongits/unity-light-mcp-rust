using System;
using System.IO;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Clients.Configurators;
using MCPForUnity.Editor.Models;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Clients
{
    [TestFixture]
    public class ConfigurationTimelineSafetyTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "mcp_config_timeline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestCase("{\"theme\":\"dark\",\"mcp\":")]
        [TestCase("[ {\"theme\":\"dark\"} ]")]
        [TestCase("null")]
        public void OpenCode_ConfigureInvalidExistingFile_PreservesEveryByte(string original)
        {
            string path = Path.Combine(_directory, "opencode.json");
            File.WriteAllText(path, original);
            var configurator = new TemporaryOpenCodeConfigurator(path);

            Assert.Throws<InvalidOperationException>(() => configurator.Configure());

            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.AreEqual(McpStatus.Error, configurator.Status);
        }

        [Test]
        public void OpenCode_StatusAutoRewrite_InvalidFileNeverBecomesEmptyConfiguration()
        {
            string path = Path.Combine(_directory, "opencode.json");
            const string original = "{\"theme\":\"dark\", // interrupted user edit";
            File.WriteAllText(path, original);
            var configurator = new TemporaryOpenCodeConfigurator(path);

            Assert.AreEqual(McpStatus.Error, configurator.CheckStatus(attemptAutoRewrite: true));
            Assert.AreEqual(original, File.ReadAllText(path));
        }

        [Test]
        public void Claude_RepeatedConfigure_RegistersWithoutTogglingToRemoval()
        {
            var configurator = new RecordingClaudeConfigurator();
            configurator.Configure();
            configurator.Configure();

            Assert.AreEqual(2, configurator.Registrations);
            Assert.AreEqual(0, configurator.Removals);
            Assert.AreEqual(McpStatus.Configured, configurator.Status);
        }

        [Test]
        public void Claude_CapturedConfigure_StatusChangesBeforeWorkerStillRegisters()
        {
            var configurator = new RecordingClaudeConfigurator();
            // Simulate a status refresh completing after the configure click was queued.
            configurator.MarkConfigured();
            configurator.ConfigureWithCapturedValues(null, null, null, true, null,
                null, null, null, null, null, ConfiguredTransport.Http);

            Assert.AreEqual(1, configurator.Registrations);
            Assert.AreEqual(0, configurator.Removals);
        }

        private sealed class TemporaryOpenCodeConfigurator : OpenCodeConfigurator
        {
            private readonly string _path;
            public TemporaryOpenCodeConfigurator(string path) { _path = path; }
            public override string GetConfigPath() => _path;
        }

        private sealed class RecordingClaudeConfigurator : ClaudeCliMcpConfigurator
        {
            public int Registrations;
            public int Removals;
            public RecordingClaudeConfigurator() : base(new McpClient { name = "Test Claude" }) { }
            public void MarkConfigured() => client.SetStatus(McpStatus.Configured);
            protected override void Register()
            {
                Registrations++;
                MarkConfigured();
            }
            public override void Unregister() { Removals++; }
            protected override void RegisterWithCapturedValues(
                string projectDir, string claudePath, string pathPrepend,
                bool useHttpTransport, string httpUrl, string uvxPath, string fromArgs,
                string packageName, string uvxDevFlags, string apiKey, ConfiguredTransport serverTransport)
            {
                Registrations++;
                MarkConfigured();
            }
        }
    }
}
