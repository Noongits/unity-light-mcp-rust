using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace AssetStoreTools.Utility
{
    internal static class FileUtility
    {
        private class RenameInfo
        {
            public string OriginalName;
            public string CurrentName;
            public bool HadMeta;
        }

        public static string AbsolutePathToRelativePath(string path, bool allowSymlinks)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            string convertedPath = path.Replace("\\", "/");

            var allPackages = PackageUtility.GetAllPackages();
            foreach (var package in allPackages)
            {
                if (Path.GetFullPath(package.resolvedPath) != Path.GetFullPath(convertedPath)
                    && !Path.GetFullPath(convertedPath).StartsWith(Path.GetFullPath(package.resolvedPath) + Path.DirectorySeparatorChar))
                    continue;

                convertedPath = Path.GetFullPath(convertedPath)
                    .Replace(Path.GetFullPath(package.resolvedPath), package.assetPath)
                    .Replace("\\", "/");

                return convertedPath;
            }

            if (convertedPath.StartsWith(Constants.RootProjectPath))
            {
                convertedPath = convertedPath.Substring(Constants.RootProjectPath.Length);
            }
            else
            {
                if (allowSymlinks && SymlinkUtil.FindSymlinkFolderRelative(convertedPath, out var symlinkPath))
                    convertedPath = symlinkPath;
            }

            return convertedPath;
        }

        public static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        {
            // Get information about the source directory
            var dir = new DirectoryInfo(sourceDir);

            // Check if the source directory exists
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            // Cache directories before we start copying
            DirectoryInfo[] dirs = dir.GetDirectories();

            // Create the destination directory
            Directory.CreateDirectory(destinationDir);

            // Get the files in the source directory and copy to the destination directory
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            // If recursive and copying subdirectories, recursively call this method
            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true);
                }
            }
        }

        public static bool ShouldHaveMeta(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            // Meta files never have other metas
            if (assetPath.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase))
                return false;

            // File System entries ending with '~' are hidden in the context of ADB
            if (assetPath.EndsWith("~"))
                return false;

            // File System entries whose names start with '.' are hidden in the context of ADB
            var assetName = assetPath.Replace("\\", "/").Split('/').Last();
            if (assetName.StartsWith("."))
                return false;

            return true;
        }

        public static bool IsMissingMetaFiles(IEnumerable<string> sourcePaths)
        {
            foreach (var sourcePath in sourcePaths)
            {
                if (!Directory.Exists(sourcePath))
                    continue;

                var allDirectories = Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories);
                foreach (var dir in allDirectories)
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (!dirInfo.Name.EndsWith("~"))
                        continue;

                    var nestedContent = dirInfo.GetFileSystemInfos("*", SearchOption.AllDirectories);
                    foreach (var nested in nestedContent)
                    {
                        if (!ShouldHaveMeta(nested.FullName))
                            continue;

                        if (!File.Exists(nested.FullName + ".meta"))
                            return true;
                    }
                }
            }

            return false;
        }

        public static void GenerateMetaFiles(IEnumerable<string> sourcePaths)
        {
            var renameInfos = new List<RenameInfo>();

            foreach (var sourcePath in sourcePaths)
            {
                if (!Directory.Exists(sourcePath))
                    continue;

                var hiddenDirectoriesInPath = Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories).Where(x => x.EndsWith("~"));
                foreach (var hiddenDir in hiddenDirectoriesInPath)
                {
                    var hiddenDirRelative = AbsolutePathToRelativePath(hiddenDir, ASToolsPreferences.Instance.EnableSymlinkSupport);
                    if (!hiddenDirRelative.StartsWith("Assets/") && !hiddenDirRelative.StartsWith("Packages/"))
                    {
                        ASDebug.LogWarning($"Path {sourcePath} is not part of the Asset Database and will be skipped");
                        continue;
                    }

                    renameInfos.Add(new RenameInfo() { CurrentName = hiddenDirRelative, OriginalName = hiddenDirRelative });
                }
            }

            if (renameInfos.Count == 0)
                return;

            var movedDirectories = new List<RenameInfo>();
            var errors = new List<Exception>();
            EditorApplication.LockReloadAssemblies();
            try
            {
                // Rename children before parents; restore successful moves in reverse order.
                renameInfos = renameInfos.OrderByDescending(x => x.OriginalName.Length).ToList();
                try
                {
                    AssetDatabase.StartAssetEditing();
                    foreach (var renameInfo in renameInfos)
                    {
                        var destination = renameInfo.OriginalName.TrimEnd('~');
                        renameInfo.HadMeta = File.Exists(destination + ".meta");
                        Directory.Move(renameInfo.OriginalName, destination);
                        renameInfo.CurrentName = destination;
                        movedDirectories.Add(renameInfo);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
                finally
                {
                    try { AssetDatabase.StopAssetEditing(); }
                    catch (Exception exception) { errors.Add(exception); }
                    try { AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); }
                    catch (Exception exception) { errors.Add(exception); }
                    try { AssetDatabase.ReleaseCachedFileHandles(); }
                    catch (Exception exception) { errors.Add(exception); }
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            finally
            {
                try
                {
                    try
                    {
                        AssetDatabase.StartAssetEditing();
                        for (var index = movedDirectories.Count - 1; index >= 0; index--)
                        {
                            var renameInfo = movedDirectories[index];
                            try
                            {
                                // Directory.Move refuses to overwrite a newly occupied original path.
                                Directory.Move(renameInfo.CurrentName, renameInfo.OriginalName);
                                if (!renameInfo.HadMeta && File.Exists(renameInfo.CurrentName + ".meta"))
                                    File.Delete(renameInfo.CurrentName + ".meta");
                            }
                            catch (Exception exception)
                            {
                                errors.Add(exception);
                            }
                        }
                    }
                    finally
                    {
                        AssetDatabase.StopAssetEditing();
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
                finally
                {
                    EditorApplication.UnlockReloadAssemblies();
                }
            }

            if (errors.Count > 0)
                throw new AggregateException("Meta generation or restoration failed. Check affected folder paths.", errors);
        }
    }
}