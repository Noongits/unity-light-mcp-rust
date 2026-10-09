using System;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Migrations
{
    /// <summary>
    /// Keeps stdio MCP clients in sync with the current package version by rewriting their configs when the package updates.
    /// </summary>
    [InitializeOnLoad]
    internal static class StdIoVersionMigration
    {
        private const string LastUpgradeKey = EditorPrefKeys.LastStdIoUpgradeVersion;

        static StdIoVersionMigration()
        {
            if (Application.isBatchMode)
                return;

            EditorApplication.delayCall += RunMigrationIfNeeded;
        }

        private static void RunMigrationIfNeeded()
        {
            EditorApplication.delayCall -= RunMigrationIfNeeded;

            string currentVersion = AssetPathUtility.GetPackageVersion();
            if (string.IsNullOrEmpty(currentVersion) || string.Equals(currentVersion, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string lastUpgradeVersion = string.Empty;
            try { lastUpgradeVersion = EditorPrefs.GetString(LastUpgradeKey, string.Empty); } catch { }

            if (string.Equals(lastUpgradeVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return; // Already refreshed for this package version
            }

            bool hadFailures = false;
            bool touchedAny = false;

            var configurators = McpClientRegistry.All.OfType<McpClientConfiguratorBase>().ToList();
            foreach (var configurator in configurators)
            {
                try
                {
                    if (!configurator.SupportsAutoConfigure)
                        continue;

                    // Handle CLI-based configurators (e.g., Claude Code CLI)
                    // CheckStatus with attemptAutoRewrite=true will auto-reregister if version mismatch
                    if (configurator is ClaudeCliMcpConfigurator cliConfigurator)
                    {
                        var previousStatus = configurator.Status;
                        configurator.CheckStatus(attemptAutoRewrite: true);
                        if (configurator.Status != previousStatus)
                        {
                            touchedAny = true;
                        }
                        continue;
                    }

                    // Handle JSON file-based configurators
                    if (!ConfigUsesStdIo(configurator.Client))
                        continue;

                    // Skip clients that don't support the current transport setting —
                    // Configure() would throw (e.g., Claude Desktop when HTTP is enabled).
                    bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                    if (useHttp && !configurator.Client.SupportsHttpTransport)
                        continue;

                    MCPServiceLocator.Client.ConfigureClient(configurator);
                    touchedAny = true;
                }
                catch (Exception ex)
                {
                    hadFailures = true;
                    McpLog.Warn($"Failed to refresh stdio config for {configurator.DisplayName}: {ex.Message}");
                }
            }

            // Failure takes precedence over an empty success count: if every rewrite
            // failed, recording this version would permanently suppress the retry.
            if (!ShouldRecordUpgrade(hadFailures))
            {
                McpLog.Warn("Stdio MCP upgrade encountered errors; will retry next session.");
                return;
            }

            if (!touchedAny)
            {
                try { EditorPrefs.SetString(LastUpgradeKey, currentVersion); } catch { }
                return;
            }

            try
            {
                EditorPrefs.SetString(LastUpgradeKey, currentVersion);
            }
            catch { }

            McpLog.Info($"Updated stdio MCP configs to package version {currentVersion}.");
        }

        internal static bool ShouldRecordUpgrade(bool hadFailures) => !hadFailures;

        private static bool ConfigUsesStdIo(McpClient client)
        {
            return JsonConfigUsesStdIo(client);
        }

        private static bool JsonConfigUsesStdIo(McpClient client)
        {
            string configPath = McpConfigurationHelper.GetClientConfigPath(client);
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                return false;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(configPath));

                JToken unityNode = null;
                if (client.IsVsCodeLayout)
                {
                    unityNode = root["servers"]?[ProductInfo.McpServerName]
                               ?? root["mcp"]?["servers"]?[ProductInfo.McpServerName];
                }
                else
                {
                    unityNode = root["mcpServers"]?[ProductInfo.McpServerName];
                }

                if (unityNode == null) return false;

                return unityNode["command"] != null;
            }
            catch (Exception ex)
            {
                // An unreadable/malformed config is an unknown migration state, not
                // evidence that there is nothing to refresh. Preserve next-session retry.
                throw new InvalidDataException("Could not inspect the client configuration for stdio migration.", ex);
            }
        }

    }
}
