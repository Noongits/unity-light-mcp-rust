using System;
using System.IO;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Handles copying a local MCPForUnity folder into the current project's package location with backup/restore support.
    /// </summary>
    public class PackageDeploymentService : IPackageDeploymentService
    {
        private const string BackupRootFolderName = "MCPForUnityDeployBackups";

        public string GetStoredSourcePath()
        {
            return EditorPrefs.GetString(EditorPrefKeys.PackageDeploySourcePath, string.Empty);
        }

        public void SetStoredSourcePath(string path)
        {
            ValidateSource(path);
            EditorPrefs.SetString(EditorPrefKeys.PackageDeploySourcePath, Path.GetFullPath(path));
        }

        public void ClearStoredSourcePath()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.PackageDeploySourcePath);
        }

        public string GetTargetPath()
        {
            // Prefer Package Manager resolved path for the installed package
            var packageInfo = PackageInfo.FindForAssembly(typeof(PackageDeploymentService).Assembly);
            if (packageInfo != null)
            {
                if (!string.IsNullOrEmpty(packageInfo.resolvedPath) && Directory.Exists(packageInfo.resolvedPath))
                {
                    return packageInfo.resolvedPath;
                }

                if (!string.IsNullOrEmpty(packageInfo.assetPath))
                {
                    string absoluteFromAsset = MakeAbsolute(packageInfo.assetPath);
                    if (Directory.Exists(absoluteFromAsset))
                    {
                        return absoluteFromAsset;
                    }
                }
            }

            // Fallback to computed package root
            string packageRoot = AssetPathUtility.GetMcpPackageRootPath();
            if (!string.IsNullOrEmpty(packageRoot))
            {
                string absolutePath = MakeAbsolute(packageRoot);
                if (Directory.Exists(absolutePath))
                {
                    return absolutePath;
                }
            }

            return null;
        }

        public string GetTargetDisplayPath()
        {
            string target = GetTargetPath();
            if (string.IsNullOrEmpty(target))
                return "Not found (check Packages/manifest.json)";
            // Use forward slashes to avoid backslash escape sequence issues in UI text
            return target.Replace('\\', '/');
        }

        public string GetLastBackupPath()
        {
            return EditorPrefs.GetString(EditorPrefKeys.PackageDeployLastBackupPath, string.Empty);
        }

        public bool HasBackup()
        {
            string path = GetLastBackupPath();
            return !string.IsNullOrEmpty(path) && Directory.Exists(path);
        }

        public PackageDeploymentResult DeployFromStoredSource()
        {
            string sourcePath = GetStoredSourcePath();
            if (string.IsNullOrEmpty(sourcePath))
            {
                return Fail("Select a MCPForUnity folder first.");
            }

            string validationError = ValidateSource(sourcePath, throwOnError: false);
            if (!string.IsNullOrEmpty(validationError))
            {
                return Fail(validationError);
            }

            string targetPath = GetTargetPath();
            if (string.IsNullOrEmpty(targetPath))
            {
                return Fail("Could not locate the installed MCP package. Check Packages/manifest.json.");
            }

            if (PathsOverlap(sourcePath, targetPath))
            {
                return Fail("Source and target must be separate, non-nested folders. Choose a different MCPForUnity folder.");
            }

            try
            {
                EditorUtility.DisplayProgressBar("Deploy MCP for Unity", "Creating backup...", 0.25f);
                string backupPath = BackupThenReplace(sourcePath, targetPath, CreateBackup,
                    (backup, target, source) =>
                    {
                        EditorPrefs.SetString(EditorPrefKeys.PackageDeployLastTargetPath, target);
                        EditorPrefs.SetString(EditorPrefKeys.PackageDeployLastSourcePath, source);
                        EditorPrefs.SetString(EditorPrefKeys.PackageDeployLastBackupPath, backup);
                    },
                    (source, target) =>
                    {
                        EditorUtility.DisplayProgressBar("Deploy MCP for Unity", "Replacing package contents...", 0.7f);
                        CopyCoreFolders(source, target);
                    });

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                return Success("Deployment completed.", sourcePath, targetPath, backupPath);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Deployment failed: {ex.Message}");
                return Fail($"Deployment failed: {ex.Message}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public PackageDeploymentResult RestoreLastBackup()
        {
            string backupPath = GetLastBackupPath();
            string targetPath = EditorPrefs.GetString(EditorPrefKeys.PackageDeployLastTargetPath, string.Empty);

            if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath))
            {
                return Fail("No backup available to restore.");
            }

            // A previous restore can fail after deleting the target. Keep its recorded
            // destination so retrying can recreate it instead of requiring it to exist.
            if (string.IsNullOrEmpty(targetPath))
            {
                targetPath = GetTargetPath();
            }

            if (string.IsNullOrEmpty(targetPath))
            {
                return Fail("Could not locate target package path.");
            }

            try
            {
                EditorUtility.DisplayProgressBar("Restore MCP for Unity", "Restoring backup...", 0.5f);
                ReplaceDirectory(backupPath, targetPath);

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                return Success("Restore completed.", null, targetPath, backupPath);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Restore failed: {ex.Message}");
                return Fail($"Restore failed: {ex.Message}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        internal static string BackupThenReplace(string source, string target,
            Func<string, string> createBackup, Action<string, string, string> publishBackup,
            Action<string, string> replace)
        {
            string backup = createBackup(target);
            // Publish recovery information before the first destructive write. A failed
            // replacement must leave the newly completed backup available to Restore.
            publishBackup(backup, target, source);
            replace(source, target);
            return backup;
        }

        internal static string CreateBackupFolderName(DateTime now)
            => $"backup_{now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

        private void CopyCoreFolders(string sourceRoot, string targetRoot)
        {
            string sourceEditor = Path.Combine(sourceRoot, "Editor");
            string sourceRuntime = Path.Combine(sourceRoot, "Runtime");

            ReplaceDirectory(sourceEditor, Path.Combine(targetRoot, "Editor"));
            ReplaceDirectory(sourceRuntime, Path.Combine(targetRoot, "Runtime"));
        }

        private static void ReplaceDirectory(string source, string destination)
        {
            if (Directory.Exists(destination))
            {
                FileUtil.DeleteFileOrDirectory(destination);
            }

            FileUtil.CopyFileOrDirectory(source, destination);
        }

        private string CreateBackup(string targetPath)
        {
            string backupRoot = Path.Combine(GetProjectRoot(), "Library", BackupRootFolderName);
            Directory.CreateDirectory(backupRoot);

            // Never delete an existing recovery point to make room for this attempt.
            string backupPath = Path.Combine(backupRoot, CreateBackupFolderName(DateTime.Now));

            FileUtil.CopyFileOrDirectory(targetPath, backupPath);
            return backupPath;
        }

        private static string ValidateSource(string sourcePath, bool throwOnError = true)
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                if (throwOnError)
                {
                    throw new ArgumentException("Source path cannot be empty.");
                }

                return "Source path is empty.";
            }

            if (!Directory.Exists(sourcePath))
            {
                if (throwOnError)
                {
                    throw new ArgumentException("Selected folder does not exist.");
                }

                return "Selected folder does not exist.";
            }

            bool hasEditor = Directory.Exists(Path.Combine(sourcePath, "Editor"));
            bool hasRuntime = Directory.Exists(Path.Combine(sourcePath, "Runtime"));

            if (!hasEditor || !hasRuntime)
            {
                string message = "Folder must contain Editor and Runtime subfolders.";
                if (throwOnError)
                {
                    throw new ArgumentException(message);
                }

                return message;
            }

            return null;
        }

        private static string MakeAbsolute(string assetPath)
        {
            assetPath = assetPath.Replace('\\', '/');

            if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            }

            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            }

            return Path.GetFullPath(assetPath);
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        internal static bool PathsOverlap(string a, string b)
        {
            string normA = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normB = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase)
                || normA.StartsWith(normB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normB.StartsWith(normA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static PackageDeploymentResult Success(string message, string source, string target, string backup)
        {
            return new PackageDeploymentResult
            {
                Success = true,
                Message = message,
                SourcePath = source,
                TargetPath = target,
                BackupPath = backup
            };
        }

        private static PackageDeploymentResult Fail(string message)
        {
            return new PackageDeploymentResult
            {
                Success = false,
                Message = message
            };
        }
    }
}
