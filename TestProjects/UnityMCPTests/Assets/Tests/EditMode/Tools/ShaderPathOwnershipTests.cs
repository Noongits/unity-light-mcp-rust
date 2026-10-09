using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    public class ShaderPathOwnershipTests
    {
        [TestCase("create", "../MCPShaderOutside")]
        [TestCase("read", "../MCPShaderOutside")]
        [TestCase("update", "../MCPShaderOutside")]
        [TestCase("delete", "../MCPShaderOutside")]
        [TestCase("create", "Assets/../../MCPShaderOutside")]
        [TestCase("create", "Assets\\..\\..\\MCPShaderOutside")]
        [TestCase("create", "C:/MCPShaderOutside")]
        public void UnsafePath_IsRejectedBeforeAnyFileOperation(string action, string path)
        {
            var result = JObject.FromObject(ManageShader.HandleCommand(new JObject
            {
                ["action"] = action,
                ["path"] = path,
                ["name"] = "OwnershipRegression",
                ["contents"] = "invalid shader"
            }));
            Assert.IsFalse(result.Value<bool>("success"));
            StringAssert.Contains("inside Assets", result.Value<string>("error"));
        }
    }
}
