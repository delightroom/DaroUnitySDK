using UnityEngine;

namespace Daro.Editor
{
    // Resolves the consumer Unity project root path. `Application.dataPath`
    // returns `<root>/Assets`, so the project root is its parent.
    // Centralized here so the path policy stays in one place — Integration
    // Manager UI, validator, bootstrap, and DaroAiKbTargets all funnel
    // through this seam.
    internal static class DaroProjectRoot
    {
        // Consumer Unity project root — the directory that contains `Assets/`,
        // `Packages/`, `ProjectSettings/`. Empty string only on the pathological
        // case of `Application.dataPath` being null (does not happen in Editor).
        internal static string Path =>
            System.IO.Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
    }
}
