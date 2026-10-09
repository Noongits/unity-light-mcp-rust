using AssetStoreTools.Validator.Data;
using AssetStoreTools.Validator.Services.Validation;
using AssetStoreTools.Validator.TestDefinitions;
using AssetStoreTools.Validator.Utility;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityObject = UnityEngine.Object;

namespace AssetStoreTools.Validator.TestMethods
{
    internal class CheckPathLengths : ITestScript
    {
        private GenericTestConfig _config;
        private IAssetUtilityService _assetUtility;

        public CheckPathLengths(GenericTestConfig config, IAssetUtilityService assetUtility)
        {
            _config = config;
            _assetUtility = assetUtility;
        }

        public TestResult Run()
        {
            TestResult result = new TestResult();

            int pathLengthLimit = 140;
            // Get all project paths and sort by length so that folders always come before files
            var allPaths = ValidatorUtility.GetProjectPaths(_config.ValidationPaths).OrderBy(x => x.Length);

            var filteredDirs = new Dictionary<string, UnityObject>();
            var filteredFiles = new Dictionary<string, UnityObject>();

            foreach (var path in allPaths)
            {
                // Truncated path examples:
                // Assets/[Scenes/SampleScene.unity]
                // Packages/com.example.package/[Editor/EditorScript.cs]
                var truncatedPath = GetContentRelativePath(path);

                // Skip paths under the character limit
                if (truncatedPath.Length < pathLengthLimit)
                    continue;

                // Skip children of already added directories
                if (filteredDirs.Keys.Any(x => path == x || path.StartsWith(x.TrimEnd('/') + "/", System.StringComparison.Ordinal)))
                    continue;

                if (AssetDatabase.IsValidFolder(path))
                {
                    filteredDirs.Add(path, _assetUtility.AssetPathToObject(path));
                    continue;
                }

                if (!filteredFiles.ContainsKey(path))
                    filteredFiles.Add(path, _assetUtility.AssetPathToObject(path));
            }

            if (filteredDirs.Count == 0 && filteredFiles.Count == 0)
            {
                result.Status = TestResultStatus.Pass;
                result.AddMessage("All package content matches the path limit criteria!");
                return result;
            }

            if (filteredDirs.Count > 0)
            {
                result.Status = TestResultStatus.VariableSeverityIssue;
                result.AddMessage($"The following folders exceed the path length limit:");
                foreach (var kvp in filteredDirs)
                {
                    result.AddMessage($"Path length: {GetContentRelativePath(kvp.Key).Length} characters", null, kvp.Value);
                }
            }

            if (filteredFiles.Count > 0)
            {
                result.Status = TestResultStatus.VariableSeverityIssue;
                result.AddMessage($"The following files exceed the path length limit:");
                foreach (var kvp in filteredFiles)
                {
                    result.AddMessage($"Path length: {GetContentRelativePath(kvp.Key).Length} characters", null, kvp.Value);
                }
            }

            return result;
        }

        private static string GetContentRelativePath(string path)
        {
            var truncatedPath = path;
            if (path.StartsWith("Assets/"))
                truncatedPath = path.Remove(0, "Assets/".Length);
            else if (path.StartsWith("Packages/"))
            {
                var splitPath = path.Split('/');
                truncatedPath = string.Join("/", splitPath.Skip(2));
            }

            return truncatedPath;
        }
    }
}
