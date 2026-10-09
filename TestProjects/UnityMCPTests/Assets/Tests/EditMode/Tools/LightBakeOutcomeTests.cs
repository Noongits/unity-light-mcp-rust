using MCPForUnity.Editor.Tools.Graphics;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    public class LightBakeOutcomeTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void RejectedBake_ReturnsErrorInsteadOfPendingOrCompleted(bool asynchronous)
        {
            int asyncCalls = 0, syncCalls = 0;
            var result = JObject.FromObject(LightBakingOps.StartBake(
                new JObject { ["async"] = asynchronous },
                () => { asyncCalls++; return false; },
                () => { syncCalls++; return false; }));
            Assert.IsFalse(result.Value<bool>("success"));
            Assert.IsNull(result["_mcp_status"]);
            Assert.AreEqual(asynchronous ? 1 : 0, asyncCalls);
            Assert.AreEqual(asynchronous ? 0 : 1, syncCalls);
        }

        [Test]
        public void AcceptedAsyncBake_RemainsPending()
        {
            var result = JObject.FromObject(LightBakingOps.StartBake(
                new JObject { ["async"] = true }, () => true,
                () => { Assert.Fail("Synchronous bake must not run"); return false; }));
            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual("pending", result.Value<string>("_mcp_status"));
        }

        [Test]
        public void RejectedAsyncBake_CanBeRetriedSuccessfully()
        {
            int calls = 0;
            System.Func<bool> start = () => ++calls > 1;
            var parameters = new JObject { ["async"] = true };
            var first = JObject.FromObject(LightBakingOps.StartBake(parameters, start, () => false));
            var second = JObject.FromObject(LightBakingOps.StartBake(parameters, start, () => false));
            Assert.IsFalse(first.Value<bool>("success"));
            Assert.AreEqual("pending", second.Value<string>("_mcp_status"));
        }
    }
}
