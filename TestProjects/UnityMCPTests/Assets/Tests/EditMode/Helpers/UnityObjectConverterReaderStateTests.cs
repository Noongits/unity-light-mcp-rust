using System.IO;
using MCPForUnity.Runtime.Serialization;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Helpers
{
    public class UnityObjectConverterReaderStateTests
    {
        [TestCase("[]")]
        [TestCase("[1, 2]")]
        [TestCase("[1, {\"nested\": [2, 3]}]")]
        public void UnsupportedArray_IsConsumedBeforeReadingTheNextMember(string unsupported)
        {
            using var text = new StringReader("{\"reference\":" + unsupported + ",\"next\":42}");
            using var reader = new JsonTextReader(text);
            Assert.IsTrue(reader.Read()); // containing object
            Assert.IsTrue(reader.Read()); // reference property
            Assert.IsTrue(reader.Read()); // unsupported array
            var converter = new UnityEngineObjectConverter();

            Assert.IsNull(converter.ReadJson(reader, typeof(GameObject), null, false, new JsonSerializer()));
            Assert.AreEqual(JsonToken.EndArray, reader.TokenType);
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(JsonToken.PropertyName, reader.TokenType);
            Assert.AreEqual("next", reader.Value);
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(42L, reader.Value);
        }

        [Test]
        public void NullReference_LeavesTheNextMemberReadable()
        {
            using var text = new StringReader("{\"reference\":null,\"next\":42}");
            using var reader = new JsonTextReader(text);
            reader.Read();
            reader.Read();
            reader.Read();
            var converter = new UnityEngineObjectConverter();
            Assert.IsNull(converter.ReadJson(reader, typeof(GameObject), null, false, new JsonSerializer()));
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(JsonToken.PropertyName, reader.TokenType);
            Assert.AreEqual("next", reader.Value);
        }
    }
}
