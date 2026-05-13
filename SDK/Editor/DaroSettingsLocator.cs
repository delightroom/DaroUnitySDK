using System;
using System.Collections.Generic;
using Daro.Internal;
using UnityEditor;
using UnityEngine;

namespace Daro.Editor
{
    // Single entry point for finding the project's DaroSettings asset.
    // Build hooks (validator / android post-processor / ios post-processor)
    // and the Integration Manager window all funnel through here so the
    // lookup policy stays in one place.
    //
    // Lookup policy (D7-A from sketch §3):
    //   1. EditorBuildSettings.GetConfigObject<DaroSettings>(ConfigKey) — O(1) primary.
    //   2. AssetDatabase.FindAssets("t:DaroSettings") — scan fallback for first run
    //      before anything has registered. On a single hit we auto-register so the
    //      next call lands on the fast path.
    //   3. No assets — return null with a diagnostic. Validator turns this into a
    //      build-blocking SETTINGS_MISSING result.
    //
    // Multi-asset (D7-B): alphabetical first wins, diagnostic lists every path.
    // Validator surfaces the situation as SETTINGS_MULTI Warn.
    //
    // Resources/ guard (D7-C): asmdef strip already keeps the type out of player
    // builds, but a settings asset placed under Assets/.../Resources/ would still
    // get included in the player. WarnIfUnderResources logs on every lookup so
    // misplacement surfaces early.
    internal static class DaroSettingsLocator
    {
        internal const string ConfigKey = "so.daro.unity/settings";

        internal static DaroSettings FindOrNull() => FindOrNull(out _);

        internal static DaroSettings FindOrNull(out string diagnosticMessage)
        {
            diagnosticMessage = null;

            if (EditorBuildSettings.TryGetConfigObject<DaroSettings>(ConfigKey, out var registered) && registered != null)
            {
                WarnIfUnderResources(AssetDatabase.GetAssetPath(registered));
                return registered;
            }

            var guids = AssetDatabase.FindAssets("t:DaroSettings");
            if (guids.Length == 0)
            {
                diagnosticMessage = "DaroSettings not found. Use Daro > Integration Manager to create one.";
                return null;
            }

            if (guids.Length == 1)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var asset = AssetDatabase.LoadAssetAtPath<DaroSettings>(path);
                WarnIfUnderResources(path);
                EditorBuildSettings.AddConfigObject(ConfigKey, asset, true);
                return asset;
            }

            var paths = new List<string>(guids.Length);
            foreach (var guid in guids)
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.Ordinal);

            diagnosticMessage = $"Multiple DaroSettings found — using '{paths[0]}'. Remove duplicates: {string.Join(", ", paths)}";

            var first = AssetDatabase.LoadAssetAtPath<DaroSettings>(paths[0]);
            WarnIfUnderResources(paths[0]);
            EditorBuildSettings.AddConfigObject(ConfigKey, first, true);
            return first;
        }

        internal static void Register(DaroSettings settings)
            => EditorBuildSettings.AddConfigObject(ConfigKey, settings, true);

        internal static void Unregister()
            => EditorBuildSettings.RemoveConfigObject(ConfigKey);

        internal static void WarnIfUnderResources(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath) && assetPath.Contains("/Resources/"))
                DaroLog.Warn("Editor", $"DaroSettings at '{assetPath}' is inside Resources/. Move it outside to avoid runtime build inclusion.");
        }
    }
}
