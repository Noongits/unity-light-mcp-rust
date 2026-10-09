using System;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Security;
using MCPForUnity.Editor.Services.AssetGen;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.AssetGen
{
    // Snapshot raw preference values (including absence), and replace only the cached store
    // reference. Reading SecureKeyStore.Current here would initialize the real OS backend.
    internal sealed class AssetGenFixtureScope : IDisposable
    {
        private readonly ISecureKeyStore previousStore;
        private readonly object previousFalChanged;
        private readonly object previousRouterChanged;
        private static readonly FieldInfo FalChanged = typeof(FalModelCatalog).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo RouterChanged = typeof(OpenRouterModelCatalog).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly Dictionary<string, string> preferences = new();

        public AssetGenFixtureScope(bool protectJobs = false)
        {
            if (protectJobs && AssetGenJobManager.RecentJobs(int.MaxValue).Count > 0)
                Assert.Ignore("Existing generation jobs must be preserved; run these fixtures in a clean test session.");
            if (FalModelCatalog.IsRefreshing("audio") || FalModelCatalog.IsRefreshing("image")
                || FalModelCatalog.IsRefreshing("model") || OpenRouterModelCatalog.IsRefreshing)
                Assert.Ignore("An existing catalog refresh must finish before running catalog fixtures.");
            previousFalChanged = FalChanged.GetValue(null);
            previousRouterChanged = RouterChanged.GetValue(null);
            previousStore = (ISecureKeyStore)typeof(SecureKeyStore)
                .GetField("_current", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            SecureKeyStore.OverrideForTests(new EnvOverlayKeyStore(new EmptyStore()));
            foreach (string kind in new[] { "audio", "image", "model" })
                foreach (string provider in new[] { "fal", "openrouter", "tripo", "meshy" })
                {
                    string key = EditorPrefKeys.AssetGenSelectedModelPrefix + kind + "." + provider;
                    preferences[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key) : null;
                    EditorPrefs.DeleteKey(key);
                }
        }

        public void Dispose()
        {
            foreach (var pair in preferences)
            {
                if (pair.Value == null) EditorPrefs.DeleteKey(pair.Key);
                else EditorPrefs.SetString(pair.Key, pair.Value);
            }
            SecureKeyStore.OverrideForTests(previousStore);
            FalChanged.SetValue(null, previousFalChanged);
            RouterChanged.SetValue(null, previousRouterChanged);
        }

        private sealed class EmptyStore : ISecureKeyStore
        {
            public bool TryGet(string providerId, out string apiKey) { apiKey = null; return false; }
            public bool Has(string providerId) => false;
            public void Set(string providerId, string apiKey) => throw new InvalidOperationException("Fixture store is read-only.");
            public void Delete(string providerId) => throw new InvalidOperationException("Fixture store is read-only.");
        }
    }
}
