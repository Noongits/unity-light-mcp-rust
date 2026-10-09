using MCPForUnity.Editor.Constants;
using System;
using Newtonsoft.Json;

namespace MCPForUnity.Editor.Models
{
    [Serializable]
    public class McpConfigServers
    {
        [JsonProperty(ProductInfo.McpServerName)]
        public McpConfigServer unityMCP;
    }
}
