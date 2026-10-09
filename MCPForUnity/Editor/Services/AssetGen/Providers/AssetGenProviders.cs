using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Security;

namespace MCPForUnity.Editor.Services.AssetGen.Providers
{
    /// <summary>
    /// Factory + registry for asset-gen provider adapters. Resolves a provider id to its adapter
    /// (model: tripo/meshy; image: fal/openrouter; audio: fal); unknown ids
    /// throw <see cref="NotSupportedException"/>. <see cref="List"/> advertises providers and reports
    /// <c>Configured</c> existence only — never a key value.
    /// </summary>
    public static class AssetGenProviders
    {
        public static IModelProviderAdapter Model(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case "tripo":
                    return new TripoAdapter();
                case "meshy":
                    return new MeshyAdapter();
                case "fal":
                    return new FalModelAdapter();
                default:
                    throw new NotSupportedException($"Unknown model provider '{id}'.");
            }
        }

        public static IImageProviderAdapter Image(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case "fal":
                    return new FalAdapter();
                case "openrouter":
                    return new OpenRouterAdapter();
                default:
                    throw new NotSupportedException($"Unknown image provider '{id}'.");
            }
        }

        public static IAudioProviderAdapter Audio(string id)
        {
            switch ((id ?? string.Empty).ToLowerInvariant())
            {
                case "fal":
                    return new FalAudioAdapter();
                default:
                    throw new NotSupportedException($"Unknown audio provider '{id}'.");
            }
        }

        public static IReadOnlyList<ProviderInfo> List()
        {
            return new List<ProviderInfo>
            {
                new ProviderInfo { Id = "tripo", Kind = "model", Configured = IsConfigured("tripo"), Capabilities = new[] { "text", "image" } },
                new ProviderInfo { Id = "meshy", Kind = "model", Configured = IsConfigured("meshy"), Capabilities = new[] { "text", "image" } },
                new ProviderInfo { Id = "fal", Kind = "model", Configured = IsConfigured("fal"), Capabilities = new[] { "text", "image" } },
                new ProviderInfo { Id = "fal", Kind = "image", Configured = IsConfigured("fal"), Capabilities = new[] { "text", "image" } },
                new ProviderInfo { Id = "openrouter", Kind = "image", Configured = IsConfigured("openrouter"), Capabilities = new[] { "text", "image" } },
                // fal's three asset kinds share one secure-store key.
                new ProviderInfo { Id = "fal", Kind = "audio", Configured = IsConfigured("fal"), Capabilities = new[] { "text", "music", "sfx" } },
            };
        }

        private static bool IsConfigured(string id)
        {
            try { return SecureKeyStore.Current.Has(id); }
            catch { return false; }
        }

        /// <summary>
        /// Standard "no key" message: points the user at the Asset Generation tab and the env override.
        /// Shared by the asset-gen tools and the job manager so the wording stays in one place.
        /// </summary>
        public static string MissingKeyMessage(string provider)
            => $"No API key configured for '{provider}'. Add it in the Unity MCP Light → Generative tab " +
               $"(or set MCPFORUNITY_{(provider ?? string.Empty).ToUpperInvariant()}_API_KEY).";
    }
}
