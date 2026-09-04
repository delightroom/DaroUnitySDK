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

            // AI KB marker axis (Codex AGENTS.md). Warn if the toggle is ON,
            // AGENTS.md exists at the project root, but its marker block is
            // missing — out-of-sync state after manual edit / orphaned
            // settings / pre-existing AGENTS.md the SDK has not yet been
            // reconciled into. Pure decision in ShouldWarnAboutAiKbMarker
            // for EditMode test seams.
            if (ShouldWarnAboutAiKbMarker(settings, DaroAiKbTargets.MarkerAllPaths()))
            {
                results.Add(new ValidationResult(
                    "any.aiKbMarker",
                    ValidationSeverity.Warn,
                    "AI integration helper toggle is ON but AGENTS.md is missing the marker block.",
                    "Re-toggle in Integration Manager, or ensure AGENTS.md exists at the project root."));
            }

            // AI KB own-file axis (Claude `.claude/rules/`, Cursor
            // `.cursor/rules/`, Cline `.clinerules/`). Warn when an
            // env-signaled target's path is occupied by a *user-authored*
            // file (not vendor-owned) or when the vendor-owned file is
            // stale (payload schema bump). Targets without env signal are
            // skipped entirely — no warn. Cline single-file conflict is
            // intentionally NOT raised as a Warn — it's a normal user
            // choice surfaced in the UI notice instead.
            if (ShouldWarnAboutAiKbOwnFile(settings))
            {
                results.Add(new ValidationResult(
                    "any.aiKbOwnFile",
                    ValidationSeverity.Warn,
                    "AI integration helper toggle is ON but one or more own-file rule paths are user-authored (not vendor-owned) or stale.",
                    "Manually remove the conflicting file at the target path, or re-toggle in Integration Manager."));
            }

            // AI KB content copy (`<project>/.daro/integration-kb/`). Warn
            // when the toggle is ON, at least one env signal exists, and
            // the KB copy directory either:
            //   - exists but is user-occupied (no vendor sentinel), or
            //   - exists, vendor-owned, but content is stale vs.
            //     `<package>/Documentation~/`.
            // Source-unavailable (package install path unresolved) is
            // logged as a Warn separately by the copier and not raised
            // again here.
            if (ShouldWarnAboutAiKbKb(settings))
            {
                results.Add(new ValidationResult(
                    "any.aiKbKb",
                    ValidationSeverity.Warn,
                    "AI integration helper toggle is ON but `.daro/integration-kb/` is user-occupied or stale.",
                    "Manually remove `.daro/integration-kb/`, or re-toggle in Integration Manager."));
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
            else
            {
                // BuildTarget.NoTarget (or other targets): platform checks skipped
                // — agnostic only. Say so, otherwise an all-green list reads as
                // "everything is configured" while the INTEGRATION KEY was never
                // looked at.
                results.Add(new ValidationResult(
                    "any.platformChecks",
                    ValidationSeverity.Warn,
                    $"Platform checks skipped — active build target is {target}.",
                    "Switch to Android or iOS in File > Build Settings, then run the checks again."));
            }

            return results;
        }

        // Test seam for the "any.aiKbMarker" check. Tests pass temp paths
        // instead of the real DaroAiKbTargets list. Returns true when the
        // toggle is ON AND at least one existing marker target file is
        // missing the marker block (out-of-sync state). If the toggle is OFF
        // or no marker targets exist at all, returns false (no Warn).
        internal static bool ShouldWarnAboutAiKbMarker(DaroSettings settings, System.Collections.Generic.IEnumerable<string> targetPaths)
        {
            if (settings == null || !settings.enableAiIntegrationHelper) return false;
            foreach (var path in targetPaths)
                if (System.IO.File.Exists(path) && !DaroAiKbInjector.HasMarker(path)) return true;
            return false;
        }

        // True when at least one env-signaled own-file target's path is
        // occupied by a user-authored file (Apply would refuse to
        // overwrite) or is stale (vendor-owned but content doesn't match
        // what Apply would write). Toggle must be ON. Targets without env
        // signal are skipped entirely — they're not in scope.
        internal static bool ShouldWarnAboutAiKbOwnFile(DaroSettings settings)
        {
            if (settings == null || !settings.enableAiIntegrationHelper) return false;
            var root = DaroProjectRoot.Path;
            foreach (var target in DaroAiKbTargets.OwnFileTargets)
            {
                if (target.EnvSignal == null || !target.EnvSignal(root)) continue;

                // Skip conflict-guarded targets — those are reflected in
                // the UI notice, not validator output.
                if (target.ConflictGuard != null &&
                    !string.IsNullOrEmpty(target.ConflictGuard(root)))
                    continue;

                var path = target.AbsolutePath;
                if (!System.IO.File.Exists(path)) continue;  // not yet created; reconciler will Create

                if (!DaroAiKbOwnFileWriter.IsOwned(path)) return true;  // user-authored at target path

                var expected = target.BodyComposer(DaroAiKbPayload.DirectiveBlock);
                if (!DaroAiKbOwnFileWriter.IsUpToDate(path, expected)) return true;  // stale vendor-owned
            }
            return false;
        }

        // True when toggle is ON, at least one env signal exists, and the
        // KB copy directory at `<project>/.daro/integration-kb/` either
        // (a) exists but lacks the vendor sentinel (user-occupied), or
        // (b) is vendor-owned but stale vs. `<package>/Documentation~/`.
        internal static bool ShouldWarnAboutAiKbKb(DaroSettings settings)
        {
            if (settings == null || !settings.enableAiIntegrationHelper) return false;
            if (!DaroAiKbTargets.AnyEnvSignal()) return false;

            var kbDir = DaroAiKbPaths.KbDirAbsolute(DaroProjectRoot.Path);
            if (!System.IO.Directory.Exists(kbDir)) return false;  // reconciler will Create

            if (!DaroAiKbKbCopier.IsOwned()) return true;          // user-occupied at KB path
            if (!DaroAiKbKbCopier.IsUpToDate()) return true;       // stale vendor copy
            return false;
        }

        private static void AddIosChecks(List<ValidationResult> results, DaroSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.iosIntegrationKey))
            {
                // Migration detection — Android 와 같은 이유(구 필드는
                // Inspector 에서 숨겨져 있어 왜 멈췄는지 안 보인다).
                var hasLegacy = !string.IsNullOrWhiteSpace(settings.iosDaroAppKey) ||
                                settings.iosKeyFile != null ||
                                !string.IsNullOrWhiteSpace(settings.iosAdMobAppId);
                if (hasLegacy)
                {
                    results.Add(new ValidationResult(
                        "ios.integrationKey.migrate",
                        ValidationSeverity.Fail,
                        "Legacy iOS app key / key file / AdMob ID detected — the unified SDK replaced them with a single INTEGRATION KEY.",
                        "Issue an INTEGRATION KEY from the Daro dashboard and enter iosIntegrationKey. The legacy fields are ignored."));
                }
                else
                {
                    results.Add(new ValidationResult(
                        "ios.integrationKey",
                        ValidationSeverity.Fail,
                        "iOS INTEGRATION KEY empty — the daro CLI cannot inject Info.plist values and the app crashes at launch.",
                        "Enter iosIntegrationKey (issued from the Daro dashboard)."));
                }
            }
            else
            {
                var shape = DaroIntegrationKeyLint.Check(settings.iosIntegrationKey);
                if (shape != DaroIntegrationKeyShape.Ok)
                {
                    results.Add(new ValidationResult(
                        "ios.integrationKey.format",
                        ValidationSeverity.Fail,
                        DaroIntegrationKeyLint.Describe(shape),
                        "Paste the INTEGRATION KEY exactly as issued (\"di\" + base64)."));
                }
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

            // GADApplicationIdentifier 체크는 없앴다 — 봉투가 admobAppKey 를
            // 실어 오고 `daro platform-key --inject` 가 심는다. 별도 입력이
            // 없으니 검사할 대상도 없다. 주입 실패는 빌드를 세우므로
            // (DaroIosPostProcessor) 누락이 조용히 지나가지 않는다.
        }

        private static void AddAndroidChecks(List<ValidationResult> results, DaroSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.androidIntegrationKey))
            {
                // Migration detection: a legacy key pair with no INTEGRATION
                // KEY means the asset predates the unified SDK — say so
                // explicitly instead of a generic "empty" error (the legacy
                // fields are hidden in the Inspector, so the user cannot see
                // why the old setup stopped working).
                var hasLegacy = !string.IsNullOrWhiteSpace(settings.androidDaroAppKey) ||
                                settings.androidKeyFile != null;
                if (hasLegacy)
                {
                    results.Add(new ValidationResult(
                        "android.integrationKey.migrate",
                        ValidationSeverity.Fail,
                        "Legacy Android app key / key file detected — the unified SDK replaced them with a single INTEGRATION KEY.",
                        "Issue an INTEGRATION KEY from the Daro dashboard and enter androidIntegrationKey. The legacy fields are ignored."));
                }
                else
                {
                    results.Add(new ValidationResult(
                        "android.integrationKey",
                        ValidationSeverity.Fail,
                        "Android INTEGRATION KEY empty — the so.daro gradle plugin fails the consumer build.",
                        "Enter androidIntegrationKey (issued from the Daro dashboard)."));
                }
            }
            else
            {
                var shape = DaroIntegrationKeyLint.Check(settings.androidIntegrationKey);
                if (shape != DaroIntegrationKeyShape.Ok)
                {
                    results.Add(new ValidationResult(
                        "android.integrationKey.format",
                        ValidationSeverity.Fail,
                        DaroIntegrationKeyLint.Describe(shape),
                        "Paste the INTEGRATION KEY exactly as issued (\"di\" + base64)."));
                }
            }
        }
    }
}
