using System.IO;
using UnityEditor;

namespace Daro.Editor
{
    // Editor startup reconciler for the AI Integration Helper.
    //
    // Runs once on Editor boot / domain reload (`[InitializeOnLoad]` → static
    // ctor) and reconciles the marker block across both target files
    // (CLAUDE.md / AGENTS.md) with the DaroSettings toggle.
    //
    // Three reconciliation paths per target:
    //   1. Toggle ON  + file exists + marker present, payload current → NoOp
    //   2. Toggle ON  + file exists + marker absent or stale          → Inject/Update
    //   3. Toggle OFF + marker present anywhere                       → Clean (all targets)
    //   4. Settings asset itself gone + marker anywhere               → Clean (orphan rescue)
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

            if (settings == null)
            {
                // Settings asset gone but marker may still be present in
                // either target — orphan rescue. Clean unconditionally
                // across both paths (Clean no-ops on missing files).
                foreach (var path in DaroAiKbTargets.AllPaths())
                    DaroAiKbInjector.Clean(path);
                return;
            }

            if (settings.enableAiIntegrationHelper)
            {
                // Only inject into existing target files (D8: never auto-create).
                foreach (var path in DaroAiKbTargets.ExistingPaths())
                    DaroAiKbInjector.Apply(path, DaroAiKbPayload.DirectiveBlock);
            }
            else
            {
                // Toggle OFF — strip marker from both targets defensively.
                foreach (var path in DaroAiKbTargets.AllPaths())
                    DaroAiKbInjector.Clean(path);
            }
        }
    }
}
