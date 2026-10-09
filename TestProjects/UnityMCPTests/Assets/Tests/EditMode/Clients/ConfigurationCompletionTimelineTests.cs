using System.IO;
using MCPForUnity.Editor.Migrations;
using MCPForUnity.External.Tommy;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Clients
{
    public class ConfigurationCompletionTimelineTests
    {
        [TestCase(1.0)]
        [TestCase(0.0)]
        [TestCase(1.2345678901234567)]
        public void FloatSerialization_PreservesTypeAndValue(double value)
        {
            var node = new TomlFloat { Value = value };
            var parsed = TOML.Parse(new StringReader("value = " + node.ToInlineToml()));
            Assert.IsTrue(parsed["value"].IsFloat);
            Assert.AreEqual(value, parsed["value"].AsFloat.Value);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Migration_FailureNeverRecordsCompletion(bool hadFailures, bool shouldRecord)
        {
            Assert.AreEqual(shouldRecord, StdIoVersionMigration.ShouldRecordUpgrade(hadFailures));
        }

        [TestCase("items = [1, 2")]
        [TestCase("items = [")]
        [TestCase("item = {key = 1")]
        [TestCase("item = {")]
        [TestCase("item = \"\"\"truncated")]
        [TestCase("item = '''")]
        public void TruncatedToml_MustNotBecomeSuccessfulPartialConfiguration(string text)
        {
            Assert.Throws<TomlParseException>(() => TOML.Parse(new StringReader(text)));
        }

        [TestCase("items = [1, 2]")]
        [TestCase("item = {key = 1}")]
        [TestCase("item = \"\"\"complete\"\"\"")]
        public void CompleteToml_StillParses(string text)
        {
            Assert.DoesNotThrow(() => TOML.Parse(new StringReader(text)));
        }
    }
}
