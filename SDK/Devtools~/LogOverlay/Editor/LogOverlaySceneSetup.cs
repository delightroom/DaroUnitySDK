#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Daro.Devtools.LogOverlay.Editor
{
    /// <summary>
    /// Drops the floating LogOverlay <see cref="UIDocument"/> into the active
    /// scene. Idempotent — re-running deletes the prior <c>LogOverlay</c>
    /// GameObject before recreating.
    /// </summary>
    /// <remarks>
    /// Assumes the bundled assets have been imported via the SDK's
    /// Integration Manager (<c>Devtools / Import Log Overlay</c>) so they
    /// land at <c>Assets/Daro Devtools/Log Overlay/</c>. The bundled
    /// PanelSettings asset already encodes <c>sortingOrder = 100</c>; if the
    /// theme reference resolved to null (consumer project missing the
    /// standard <c>UnityDefaultRuntimeTheme.tss</c>), the menu searches for
    /// any available <c>ThemeStyleSheet</c> and patches it in so the panel
    /// actually renders.
    /// </remarks>
    public static class LogOverlaySceneSetup
    {
        private const string GameObjectName     = "LogOverlay";
        private const string MenuPath           = "Daro/Devtools/Setup Log Overlay in Scene";
        private const string AssetRoot          = "Assets/Daro Devtools/Log Overlay";
        private const string PanelSettingsPath  = AssetRoot + "/Log Overlay Panel Settings.asset";
        private const string UxmlPath           = AssetRoot + "/LogOverlay.uxml";
        private const string DefaultThemePath   = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss";

        [MenuItem(MenuPath)]
        public static void Setup()
        {
            var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panelSettings == null)
            {
                Debug.LogError(
                    $"[Daro] LogOverlay PanelSettings missing at {PanelSettingsPath}. " +
                    "Open Daro Integration Manager → Devtools → Import Log Overlay first.");
                return;
            }

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogError(
                    $"[Daro] LogOverlay UXML missing at {UxmlPath}. " +
                    "Open Daro Integration Manager → Devtools → Import Log Overlay first.");
                return;
            }

            EnsureTheme(panelSettings);

            // Idempotent: nuke any prior LogOverlay GameObject (including
            // inactive — Find skips inactive, so use the broad query).
            foreach (var doc in Object.FindObjectsByType<UIDocument>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (doc.gameObject.name == GameObjectName)
                {
                    Object.DestroyImmediate(doc.gameObject);
                }
            }

            var go = new GameObject(GameObjectName);
            var docComp = go.AddComponent<UIDocument>();
            docComp.panelSettings = panelSettings;
            docComp.visualTreeAsset = uxml;
            go.AddComponent<LogOverlayController>();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[Daro] Built {GameObjectName} (PanelSettings.sortingOrder={panelSettings.sortingOrder}).");
        }

        /// <summary>
        /// PanelSettings without a <c>themeStyleSheet</c> renders nothing —
        /// UI Toolkit refuses to draw without a theme. If the bundled
        /// reference resolved to null on the consumer's project (e.g. no
        /// URP template, no auto-created <c>UnityDefaultRuntimeTheme</c>),
        /// fall back to whichever theme we can find and persist the fix.
        /// </summary>
        private static void EnsureTheme(PanelSettings panelSettings)
        {
            if (panelSettings.themeStyleSheet != null) return;

            // Prefer the URP-default location if present.
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(DefaultThemePath);
            if (theme == null)
            {
                var guids = AssetDatabase.FindAssets("t:ThemeStyleSheet");
                if (guids.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(path);
                }
            }

            if (theme == null)
            {
                Debug.LogWarning(
                    "[Daro] No ThemeStyleSheet found in project — LogOverlay PanelSettings " +
                    "will not render until a theme is assigned. UI Toolkit projects auto-create " +
                    "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss on first use; " +
                    "open any UXML in the UI Builder once to materialise it.");
                return;
            }

            panelSettings.themeStyleSheet = theme;
            EditorUtility.SetDirty(panelSettings);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
