using Daro.Internal;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Daro.Editor
{
    // IPreprocessBuildWithReport entrypoint — last-line validation gate before
    // Unity hands off to EDM4U + native post-processors. Single responsibility:
    //
    //   Validate. Any Fail → BuildFailedException → build stops with the
    //   offending CheckId in the Build Report. Warn → DaroLog.Warn (helper-routed).
    //
    // EDM4U force-resolve is intentionally NOT called here. EDM4U's own
    // Auto-Resolution + Auto-resolve-on-Build (default-enabled) cover the
    // dep sync at ~callbackOrder 45. Our previous explicit reflection call
    // was redundant and produced project-state side effects (e.g. Resolver
    // failing to copy mainTemplate.gradle when the Custom Template toggle
    // was off). Manual force-resolve still surfaces in the IM window via
    // DaroEdmChecker.TryForceResolveAndroid/Ios — that path is user-driven.
    //
    // The IPreprocessBuildWithReport surface (OnPreprocessBuild) only forwards
    // platform info to internal Run for testability — Run accepts plain
    // (DaroSettings, BuildTarget) so unit tests don't need to construct a
    // BuildReport.
    public sealed class DaroBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            Run(DaroSettingsLocator.FindOrNull(), report.summary.platform);
        }

        // testable seam — see plan D7-O.
        internal static void Run(DaroSettings settings, BuildTarget target)
        {
            var results = DaroSettingsValidator.Validate(settings, target);

            foreach (var r in results)
            {
                if (r.Severity == ValidationSeverity.Fail)
                {
                    throw new BuildFailedException(
                        $"[Daro] {r.CheckId}: {r.Message}\nFix: {r.FixHint}");
                }

                if (r.Severity == ValidationSeverity.Warn)
                {
                    DaroLog.Warn("Build", $"{r.CheckId}: {r.Message}");
                }
            }
        }
    }
}
