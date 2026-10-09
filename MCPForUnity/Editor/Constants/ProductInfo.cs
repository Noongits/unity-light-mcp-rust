namespace MCPForUnity.Editor.Constants
{
    /// <summary>Canonical user-facing product identity strings.</summary>
    public static class ProductInfo
    {
        public const string ProductName = "Unity MCP Light";
        /// <summary>Server key written into MCP client configs (Claude Code, Codex, Cursor, ...).</summary>
        public const string McpServerName = "unity-mcp-light";
        /// <summary>Default uvx --from source for the Python server: this fork's Server/ on main.</summary>
        public const string ServerPackageSource = "git+https://github.com/Talhasarac/unity-mcp-light@main#subdirectory=Server";
        public const string MenuRoot = "Window/Unity MCP Light";
    }
}
