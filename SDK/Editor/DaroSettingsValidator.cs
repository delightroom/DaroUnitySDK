using System.Collections.Generic;
using UnityEditor;

namespace Daro.Editor
{
    internal enum ValidationSeverity
    {
        Pass = 0,
        Warn = 1,
        Fail = 2,
    }

    // Single result row produced by DaroSettingsValidator. CheckId is the stable
    // dot.style key used by both the build hook (BuildFailedException prefix) and
    // the Integration Manager window (per-row rendering + field highlighting).
    internal readonly struct ValidationResult
    {
        internal readonly string CheckId;
        internal readonly ValidationSeverity Severity;
        internal readonly string Message;
        internal readonly string FixHint;

        internal ValidationResult(string checkId, ValidationSeverity severity, string message, string fixHint = "")
        {
            CheckId = checkId;
            Severity = severity;
            Message = message;
            FixHint = fixHint;
        }
    }

    // Pure decision function — single entry, no Locator dependency, no global state.
    // Caller (BuildValidator / IM window) supplies the settings instance so that:
    //   1. The same Validator can run against a hypothetical/in-memory settings
    //      object during IM "what-if" preview.
    //   2. SETTINGS_MULTI(다중 자산 detect)는 Locator의 책임 — Validator는 cycle을 만들지 않는다.
    //
    // BuildTarget.NoTarget으로 호출 시 platform-agnostic 체크만 발화 (IM 초기 로드).
    internal static class DaroSettingsValidator
    {
        internal static IReadOnlyList<ValidationResult> Validate(DaroSettings settings, BuildTarget target)
        {
            var results = new List<ValidationResult>();

            if (settings == null)
            {
                results.Add(new ValidationResult(
                    "any.settings",
                    ValidationSeverity.Fail,
                    "DaroSettings asset not found.",
                    "Create via Daro > Integration Manager."));
                return results;
            }

            // -- Platform-agnostic checks --------------------------------------

            var path = AssetDatabase.GetAssetPath(settings);
            if (!string.IsNullOrEmpty(path) && path.Contains("/Resources/"))
            {
                results.Add(new ValidationResult(
                    "any.resourcesPath",
                    ValidationSeverity.Warn,
                    $"DaroSettings under Resources/ ({path}). Editor fields will ship in runtime build.",
                    "Move outside Resources/."));
            }

            results.Add(new ValidationResult(
                "any.mediation",
                ValidationSeverity.Pass,
                $"Mediation: {settings.mediation}"));

            if (!DaroEdmChecker.IsEdmPresent())
            {
                results.Add(new ValidationResult(
                    "any.edm4u",
                    ValidationSeverity.Warn,
                    "EDM4U not detected. Native deps may not resolve.",
                    "Install com.google.external-dependency-manager."));
            }

            // AI KB marker — Warn if the toggle is ON but at least one existing
            // agent-instruction file (CLAUDE.md / AGENTS.md) is missing the
            // marker block (out-of-sync state after manual edit / file deletion
            // / orphaned settings). Pure decision in ShouldWarnAboutAiKbMarker
            // for EditMode test seams.
            if (ShouldWarnAboutAiKbMarker(settings, DaroAiKbTargets.AllPaths()))
            {
                results.Add(new ValidationResult(
                    "any.aiKbMarker",
                    ValidationSeverity.Warn,
                    "AI integration helper toggle is ON but at least one agent-instruction file (CLAUDE.md / AGENTS.md) is missing the marker block.",
                    "Re-toggle in Integration Manager, or ensure CLAUDE.md or AGENTS.md exists at the project root."));
            }

            // -- Platform-specific checks --------------------------------------

            if (target == BuildTarget.iOS)
            {
                AddIosChecks(results, settings);
            }
            else if (target == BuildTarget.Android)
            {
                AddAndroidChecks(results, settings);
            }
            // BuildTarget.NoTarget (or other targets): platform checks skipped — agnostic only.

            return results;
        }

        // Test seam for the "any.aiKbMarker" check. Tests pass temp paths
        // instead of the real DaroAiKbTargets list. Returns true when the
        // toggle is ON AND at least one existing target file is missing the
        // marker (out-of-sync state). If the toggle is OFF or no targets
        // exist at all, returns false (no Warn). The "no targets exist"
        // case is surfaced by the UI notice instead.
        internal static bool ShouldWarnAboutAiKbMarker(DaroSettings settings, System.Collections.Generic.IEnumerable<string> targetPaths)
        {
            if (settings == null || !settings.enableAiIntegrationHelper) return false;
            foreach (var path in targetPaths)
                if (System.IO.File.Exists(path) && !DaroAiKbInjector.HasMarker(path)) return true;
            return false;
        }

        private static void AddIosChecks(List<ValidationResult> results, DaroSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.iosDaroAppKey))
            {
                results.Add(new ValidationResult(
                    "ios.daroAppKey",
                    ValidationSeverity.Fail,
                    "iOS Daro App Key empty — native fatalError at launch.",
                    "Enter iosDaroAppKey."));
            }

            if (settings.iosKeyFile == null)
            {
                results.Add(new ValidationResult(
                    "ios.keyFile",
                    ValidationSeverity.Fail,
                    "ios-daro-key.txt not assigned.",
                    "Assign in DaroSettings > iOS Key File."));
            }

            // ATT description Fail — empty value causes App Store rejection (D7-I).
            // Building the binary without this is a shipping defect, not a soft warning.
            if (string.IsNullOrWhiteSpace(settings.attPromptDescription))
            {
                results.Add(new ValidationResult(
                    "ios.attDescription",
                    ValidationSeverity.Fail,
                    "ATT description empty — App Store will reject binary.",
                    "Enter attPromptDescription."));
            }

            // GADApplicationIdentifier (iosAdMobAppId) Fail — daro-m
            // transitively links GoogleMobileAds framework via the AppLovin
            // google-adapter; missing Info.plist key crashes the app at
            // launch. Daro 가이드 명시 + Apple/Google 강제 요구.
            if (string.IsNullOrWhiteSpace(settings.iosAdMobAppId))
            {
                results.Add(new ValidationResult(
                    "ios.admobAppId",
                    ValidationSeverity.Fail,
                    "AdMob Key empty — GADApplicationIdentifier missing → app crashes at launch.",
                    "Enter iosAdMobAppId (AdMob console → App Settings → App ID)."));
            }
        }

        private static void AddAndroidChecks(List<ValidationResult> results, DaroSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.androidDaroAppKey))
            {
                results.Add(new ValidationResult(
                    "android.daroAppKey",
                    ValidationSeverity.Fail,
                    "Android Daro App Key empty — init fails with daroErrorCode=-3.",
                    "Enter androidDaroAppKey."));
            }

            if (settings.androidKeyFile == null)
            {
                results.Add(new ValidationResult(
                    "android.keyFile",
                    ValidationSeverity.Fail,
                    "android-daro-key.txt not assigned.",
                    "Assign in DaroSettings > Android Key File."));
            }
        }
    }
}
