#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
// UnityEditor.PackageInfo (legacy) collides with UnityEditor.PackageManager.PackageInfo;
// alias to the UPM one so plain `PackageInfo` references inside this file resolve correctly.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Daro.Editor.Devtools
{
    /// <summary>
    /// SDK-developer-only menu that rebuilds the bundled
    /// <c>LogOverlay.unitypackage</c> from the source tree at
    /// <c>SDK/Devtools~/LogOverlay/</c> so the binary artifact at
    /// <c>SDK/Devtools~/LogOverlay.unitypackage</c> stays in sync with
    /// source edits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Source lives in a <c>~</c> folder.</b> Both the source tree and
    /// the binary artifact sit under <c>SDK/Devtools~/</c>; Unity skips
    /// <c>~</c> folders during AssetDatabase import so the source-truth
    /// asmdefs (<c>Daro.Devtools.LogOverlay.{Runtime,Editor}</c>) only
    /// register once — at the consumer-imported location
    /// (<c>Assets/Daro Devtools/Log Overlay/</c>). If the source were
    /// visible to AssetDatabase, importing the .unitypackage would yield
    /// a "Assembly with name already exists" collision against the
    /// source-truth copy.
    /// </para>
    /// <para>
    /// <b>.meta files preserved across rebuilds.</b> The source-truth
    /// <c>.meta</c> files (committed to git) carry stable GUIDs. The
    /// rebuild copies them verbatim into staging via raw
    /// <see cref="File"/> I/O so AssetDatabase sees the staged tree
    /// with the same GUIDs every build. Consumer re-import after an SDK
    /// update therefore lands on the same GUIDs and Unity treats it as
    /// an in-place update of the prior import (rather than spawning
    /// duplicate asset entries).
    /// </para>
    /// <para>
    /// <b>Safety</b>: refuses to run if
    /// <c>Assets/Daro Devtools/Log Overlay/</c> already exists in the
    /// active project, to avoid clobbering a developer's existing
    /// consumer-flow import. Delete the folder manually and retry.
    /// </para>
    /// </remarks>
    public static class DaroLogOverlayPackager
    {
        private const string SdkPackageName = "so.daro.unity";

        // Source inside SDK/Devtools~/ (tilde = AssetDatabase-hidden).
        private const string SourceRel        = "Devtools~/LogOverlay";
        private const string StagingAssetPath = "Assets/Daro Devtools/Log Overlay";
        private const string OutputRel        = "Devtools~/LogOverlay.unitypackage";

        private const string MenuPath = "Daro/Devtools/Rebuild LogOverlay Package";

        [MenuItem(MenuPath)]
        public static void Build()
        {
            if (!TryResolveSdkPath(out var sdkResolvedPath))
            {
                Debug.LogError(
                    $"[Daro] Could not resolve the '{SdkPackageName}' UPM package. " +
                    "Open this menu from a project that has the Daro Unity SDK linked.");
                return;
            }

            // Source is in a `~` folder — AssetDB doesn't track it, so
            // FileUtil / AssetDatabase APIs don't apply. Use raw System.IO.
            var sourceAbs = Path.Combine(sdkResolvedPath, SourceRel);
            if (!Directory.Exists(sourceAbs))
            {
                Debug.LogError($"[Daro] LogOverlay source not found at {sourceAbs}.");
                return;
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
            var stagingAbs  = Path.Combine(projectRoot, StagingAssetPath);
            if (Directory.Exists(stagingAbs))
            {
                Debug.LogError(
                    $"[Daro] Staging path '{StagingAssetPath}' already exists in this project. " +
                    "Delete it (or move it aside) and rerun.");
                return;
            }

            var outputAbs = Path.Combine(sdkResolvedPath, OutputRel);
            Directory.CreateDirectory(Path.GetDirectoryName(outputAbs)!);

            try
            {
                CopyDirectoryRecursive(sourceAbs, stagingAbs);
                AssetDatabase.Refresh();

                AssetDatabase.ExportPackage(
                    StagingAssetPath,
                    outputAbs,
                    ExportPackageOptions.Recurse);

                Debug.Log($"[Daro] Built LogOverlay package → {outputAbs}");
            }
            finally
            {
                // Cleanup via AssetDatabase keeps .meta + AssetDB state
                // consistent (raw Directory.Delete would leave stale .meta).
                if (AssetDatabase.IsValidFolder(StagingAssetPath))
                {
                    AssetDatabase.DeleteAsset(StagingAssetPath);
                }
                // Drop the empty parent folder we created if no siblings remain.
                var stagingParent = Path.GetDirectoryName(StagingAssetPath)!;
                if (AssetDatabase.IsValidFolder(stagingParent))
                {
                    var stagingParentAbs = Path.Combine(projectRoot, stagingParent);
                    if (Directory.Exists(stagingParentAbs) &&
                        Directory.GetFileSystemEntries(stagingParentAbs).Length == 0)
                    {
                        AssetDatabase.DeleteAsset(stagingParent);
                    }
                }
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Recursive directory copy — preserves .meta files (Unity GUIDs) by
        /// copying everything verbatim. Skips <c>.DS_Store</c>; never
        /// follows symlinks (we don't expect any in the source tree).
        /// </summary>
        private static void CopyDirectoryRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var srcFile in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(srcFile);
                if (name == ".DS_Store") continue;
                File.Copy(srcFile, Path.Combine(dst, name), overwrite: false);
            }
            foreach (var srcDir in Directory.GetDirectories(src))
            {
                var name = Path.GetFileName(srcDir);
                CopyDirectoryRecursive(srcDir, Path.Combine(dst, name));
            }
        }

        /// <summary>
        /// Resolve the absolute disk path of the Daro Unity SDK UPM package.
        /// Works whether the SDK is consumed via file: dep (embedded /
        /// local) or installed from a registry.
        /// </summary>
        private static bool TryResolveSdkPath(out string resolvedPath)
        {
            var info = PackageInfo.FindForAssetPath($"Packages/{SdkPackageName}/package.json");
            if (info != null)
            {
                resolvedPath = info.resolvedPath;
                return true;
            }
            resolvedPath = "";
            return false;
        }
    }
}
#endif
