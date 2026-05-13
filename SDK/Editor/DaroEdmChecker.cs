using System;
using System.Reflection;
using UnityEditor;

namespace Daro.Editor
{
    // EDM4U bridge. All EDM4U calls go through reflection so Daro.Editor.asmdef
    // does NOT hard-link to EDM4U — a consumer project that hasn't installed
    // `com.google.external-dependency-manager` still compiles cleanly, and the
    // Validator's EDM_MISSING check is the user-visible failure surface instead.
    //
    // Three force-resolve entry points (sketch §7, patched):
    //   - TryForceResolveAndroid   — IM window's Android button + build hook auto-trigger for Android target
    //   - TryForceResolveIos       — IM window's iOS button + build hook auto-trigger for iOS target
    //   - TryForceResolveFor(target) — dispatcher used by build hook so a single-platform build
    //                                  only resolves that platform's deps (no double work)
    //
    // Single combined entry was rejected — the IM's separate buttons would be a
    // UX lie (each click would actually run both), and the build hook's
    // auto-trigger would do double work on every build.
    internal static class DaroEdmChecker
    {
        // EDM4U exposes its public menus on these types. Names are stable across
        // EDM4U 1.2.x; if a future version renames, IsEdmPresent's fallback scan
        // and the Validator's EDM_MISSING check still surface the breakage.
        private const string AndroidResolverType = "GooglePlayServices.PlayServicesResolver, Google.JarResolver";
        private const string AndroidResolverMethod = "MenuResolve";
        private const string IosResolverType = "Google.IOSResolver, Google.IOSResolver";
        private const string IosResolverMethod = "MenuInstallCocoapods";

        // EDM4U presence detection — used by Validator for EDM_MISSING Fail and
        // by the IM window's status panel.
        internal static bool IsEdmPresent()
        {
            // Primary: scan loaded assemblies. EDM4U registers Google.VersionHandler
            // and Google.JarResolver at editor startup, so a single AppDomain pass
            // detects it for any project that has imported the package.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name == "Google.VersionHandler" || name == "Google.JarResolver")
                    return true;
            }

            // Fallback: assets-on-disk scan. Catches the rare case where EDM4U
            // assets are in the project but assemblies haven't loaded yet
            // (e.g. mid-import). Safe even if Unity hasn't finished compiling.
            return AssetDatabase.FindAssets("Google.VersionHandler").Length > 0;
        }

        internal static void TryForceResolveAndroid()
            => InvokeStatic(AndroidResolverType, AndroidResolverMethod);

        internal static void TryForceResolveIos()
        {
#if !UNITY_EDITOR_WIN
            InvokeStatic(IosResolverType, IosResolverMethod);
#endif
            // Windows: CocoaPods isn't available — silent no-op by compile-time guard.
        }

        // Dispatcher used by build hook (auto-trigger before validator) and by IM
        // when the user wants "resolve everything for current target".
        internal static void TryForceResolveFor(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android:
                    TryForceResolveAndroid();
                    break;
                case BuildTarget.iOS:
                    TryForceResolveIos();
                    break;
                // Other targets: no-op — Daro deps only resolve for Android/iOS.
            }
        }

        // Single reflection helper. Type or method missing → silent no-op (EDM4U
        // absent or renamed). Reflection-thrown exceptions are also swallowed —
        // the Validator's EDM_MISSING is the fail-loud channel.
        private static void InvokeStatic(string assemblyQualifiedTypeName, string methodName)
        {
            try
            {
                var type = Type.GetType(assemblyQualifiedTypeName);
                var method = type?.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public);
                method?.Invoke(null, null);
            }
            catch
            {
                // Intentional: EdmChecker is a "best effort" bridge. Build pipeline
                // failures must surface through Validator (EDM_MISSING) or through
                // EDM4U's own resolve output, not through reflection plumbing.
            }
        }
    }
}
