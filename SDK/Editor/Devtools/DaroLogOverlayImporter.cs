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
    /// Consumer-side entry point for importing the bundled LogOverlay
    /// devtool. Resolves the path of <c>LogOverlay.unitypackage</c> inside
    /// the Daro Unity SDK package cache and hands it to
    /// <see cref="AssetDatabase.ImportPackage(string, bool)"/> which surfaces
    /// Unity's native Import Package dialog (file checkboxes, conflict
    /// resolution, GUID restoration).
    /// </summary>
    /// <remarks>
    /// Invoked from the <c>Daro Integration Manager</c> window's
    /// <c>Devtools</c> foldout. Files land at
    /// <c>Assets/Daro Devtools/Log Overlay/</c>; consumers can modify in
    /// place (namespace, prefix, etc.) post-import.
    /// </remarks>
    internal static class DaroLogOverlayImporter
    {
        private const string SdkPackageName = "so.daro.unity";
        private const string PackageRel     = "Devtools~/LogOverlay.unitypackage";

        // Display name Unity reports in importPackage* callbacks = the
        // .unitypackage file stem. Filter on it so the first-run hint only
        // fires for our import, not a coincidental import of another package.
        private const string PackageDisplayName = "LogOverlay";

        /// <summary>
        /// Importable when the SDK package is installed AND its devtool
        /// asset is present (binary may be absent in dev branches before
        /// the first <c>Rebuild LogOverlay Package</c>).
        /// </summary>
        internal static bool IsAvailable(out string resolvedPath, out string reasonKey)
        {
            resolvedPath = "";
            reasonKey    = "";

            var info = PackageInfo.FindForAssetPath($"Packages/{SdkPackageName}/package.json");
            if (info == null)
            {
                reasonKey = "devtools.logOverlay.notAvailable.packageMissing";
                return false;
            }

            var candidate = Path.Combine(info.resolvedPath, PackageRel);
            if (!File.Exists(candidate))
            {
                reasonKey = "devtools.logOverlay.notAvailable.assetMissing";
                return false;
            }

            resolvedPath = candidate;
            return true;
        }

        /// <summary>
        /// Trigger the import. Returns true if the package path was found
        /// and handed to <see cref="AssetDatabase.ImportPackage(string, bool)"/>;
        /// false if not available. The actual import is asynchronous (user
        /// confirms in Unity's dialog) so success here only means "dialog
        /// opened".
        /// </summary>
        internal static bool Import()
        {
            if (!IsAvailable(out var path, out _))
            {
                Debug.LogError(
                    $"[Daro] LogOverlay devtool unavailable — '{PackageRel}' missing from " +
                    $"the {SdkPackageName} package cache.");
                return false;
            }

            // First-run hint: after import the consumer must set
            // LogOverlayController.consumerLogPrefix to their own log prefix
            // or their app lines fall into "Unknown" and the App / Ad Event
            // filters look empty (SDK lines show regardless). The bundled
            // setup can't know the prefix, so surface it on import-complete.
            // Re-arm cleanly in case a prior dialog was dismissed.
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            AssetDatabase.importPackageFailed    -= OnImportFailed;
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            AssetDatabase.importPackageFailed    += OnImportFailed;

            // interactive: true → Unity's standard "Import Unity Package" dialog
            // appears so the user can review file paths and unselect specific
            // assets if desired. Matches the UX of any other .unitypackage import.
            AssetDatabase.ImportPackage(path, interactive: true);
            return true;
        }

        private static void Unsubscribe()
        {
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            AssetDatabase.importPackageFailed    -= OnImportFailed;
        }

        private static void OnImportCompleted(string packageName)
        {
            if (packageName != PackageDisplayName) return;
            Unsubscribe();
            Debug.Log(
                "[Daro] LogOverlay imported. To see your app's own logs under the " +
                "\"App\" / \"Ad Event\" filters, set 'Consumer Log Prefix' on the " +
                "LogOverlay GameObject's LogOverlayController to your log prefix " +
                "(e.g. \"[MyApp]\"). Daro SDK lines show regardless.");
        }

        private static void OnImportCancelled(string packageName)
        {
            if (packageName != PackageDisplayName) return;
            Unsubscribe();
        }

        private static void OnImportFailed(string packageName, string errorMessage)
        {
            if (packageName != PackageDisplayName) return;
            Unsubscribe();
            Debug.LogError($"[Daro] LogOverlay import failed: {errorMessage}");
        }
    }
}
#endif
