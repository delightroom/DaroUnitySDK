using UnityEditor;

namespace Daro.Editor
{
    // Editor startup entry point for the AI Integration Helper. The
    // reconcile sequence itself lives in `DaroAiKbReconciler`; this class
    // only resolves the toggle state from settings and dispatches.
    //
    // Why `delayCall` instead of running synchronously in the static ctor:
    // `[InitializeOnLoad]` fires while Unity is still constructing
    // AssetDatabase and EditorBuildSettings. Touching
    // `DaroSettingsLocator.FindOrNull()` too early can return false-null.
    // Defer one tick so the Editor is settled.
    [InitializeOnLoad]
    internal static class DaroAiKbBootstrap
    {
        static DaroAiKbBootstrap()
        {
            EditorApplication.delayCall += Reconcile;
        }

        private static void Reconcile()
        {
            var settings = DaroSettingsLocator.FindOrNull();
            var toggleOn = settings != null && settings.enableAiIntegrationHelper;
            DaroAiKbReconciler.ReconcileSync(toggleOn);
        }
    }
}
