using System;
using System.Collections.Generic;
using UnityEditor;

namespace Daro.Editor
{
    // Tiny key/value localizer for the Integration Manager window.
    // Scope: IM window only — BuildValidator logs and BuildFailedException
    // text stay English so CI / build reports stay grep-friendly.
    //
    // Two languages: English / Korean. Persistence: EditorPrefs string under
    // PrefKey. Lookup is straight Dictionary; missing key returns the key
    // itself so unhandled strings are visible (rather than blank) at runtime.
    internal static class DaroImLocalization
    {
        internal enum Lang
        {
            English = 0,
            Korean  = 1,
        }

        private const string PrefKey = "daro.im.lang";

        internal static event Action Changed;

        internal static Lang Current
        {
            get => (Lang)EditorPrefs.GetInt(PrefKey, (int)Lang.English);
            set
            {
                if (value == Current) return;
                EditorPrefs.SetInt(PrefKey, (int)value);
                Changed?.Invoke();
            }
        }

        internal static string Get(string key)
        {
            var dict = Current == Lang.Korean ? _ko : _en;
            return dict.TryGetValue(key, out var v) ? v : key;
        }

        internal static string Format(string key, params object[] args)
        {
            var template = Get(key);
            return args == null || args.Length == 0
                ? template
                : string.Format(template, args);
        }

        // Test seam — keys in EN are the canonical set.
        internal static IReadOnlyCollection<string> EnglishKeys => _en.Keys;
        internal static IReadOnlyCollection<string> KoreanKeys => _ko.Keys;

        private static readonly Dictionary<string, string> _en = new Dictionary<string, string>
        {
            // --- window chrome ---
            ["window.title"]              = "Daro Integration Manager",
            ["nosettings.warning"]        = "No DaroSettings asset found. Create one to get started.",
            ["nosettings.create"]         = "Create Settings Asset",
            ["foldout.settings"]          = "Settings",
            ["foldout.validation"]        = "Build Validation",
            ["foldout.aiHelper"]          = "AI Integration Helper",
            ["foldout.devtools"]          = "Devtools",
            ["section.mediation"]         = "Mediation",
            ["section.ios"]               = "iOS",
            ["section.android"]           = "Android",
            ["field.mediation"]           = "Mediation Variant",
            ["field.integrationKey"]      = "INTEGRATION KEY",
            ["field.attDescription"]      = "ATT Description",
            ["btn.runChecks"]             = "Run Checks",
            ["btn.validateKey"]           = "Validate Key",
            ["validate.idle"]             = "",
            ["validate.ik.ok"]            = "✓ Shape OK — real validation happens at build time (the so.daro gradle plugin decrypts it).",
            ["validate.ik.empty"]         = "✗ INTEGRATION KEY is empty.",
            ["validate.ik.legacyAppKey"]  = "✗ This looks like a legacy app key (UUID) — issue an INTEGRATION KEY from the Daro dashboard.",
            ["validate.ik.missingPrefix"] = "✗ INTEGRATION KEY must start with \"di\".",
            ["validate.ik.invalidBase64"] = "✗ Payload is not valid base64 — likely truncated or altered.",
            ["validate.ik.tooShort"]      = "✗ Payload too short — likely truncated.",
            ["assetPath.unsaved"]         = "(unsaved)",
            ["os.windowsWarning"]         = "Running on Windows — iOS build validation is host-limited.",
            ["error.uiAssetsMissing"]     = "Daro Integration Manager — UI assets missing. Reimport so.daro.unity package.",

            // --- validator messages (msg + hint per CheckId) ---
            ["v.any.settings.msg"]        = "DaroSettings asset not found.",
            ["v.any.settings.hint"]       = "Create via Daro > Integration Manager.",
            ["v.any.resourcesPath.msg"]   = "DaroSettings under Resources/ ({0}). Editor fields will ship in runtime build.",
            ["v.any.resourcesPath.hint"]  = "Move outside Resources/.",
            ["v.any.mediation.msg"]       = "Mediation: {0}",
            ["v.any.mediation.hint"]      = "",
            ["v.any.edm4u.msg"]           = "EDM4U not detected. Native deps may not resolve.",
            ["v.any.edm4u.hint"]          = "Install com.google.external-dependency-manager.",
            ["v.ios.integrationKey.msg"]          = "iOS INTEGRATION KEY empty — the daro CLI cannot inject Info.plist values and the app crashes at launch.",
            ["v.ios.integrationKey.hint"]         = "Enter iosIntegrationKey (issued from the Daro dashboard).",
            ["v.ios.integrationKey.migrate.msg"]  = "Legacy iOS app key / key file / AdMob ID detected — the unified SDK replaced them with a single INTEGRATION KEY.",
            ["v.ios.integrationKey.migrate.hint"] = "Issue an INTEGRATION KEY from the Daro dashboard and enter iosIntegrationKey. The legacy fields are ignored.",
            ["v.ios.integrationKey.format.hint"]  = "Paste the INTEGRATION KEY exactly as issued (\"di\" + base64).",
            ["v.ios.attDescription.msg"]  = "ATT description empty — App Store will reject binary.",
            ["v.ios.attDescription.hint"] = "Enter attPromptDescription.",
            ["v.android.integrationKey.msg"]          = "Android INTEGRATION KEY empty — the so.daro gradle plugin fails the consumer build.",
            ["v.android.integrationKey.hint"]         = "Enter androidIntegrationKey (issued from the Daro dashboard).",
            ["v.android.integrationKey.migrate.msg"]  = "Legacy Android app key / key file detected — the unified SDK replaced them with a single INTEGRATION KEY.",
            ["v.android.integrationKey.migrate.hint"] = "Issue an INTEGRATION KEY from the Daro dashboard and enter androidIntegrationKey. The legacy fields are ignored.",
            ["v.android.integrationKey.format.hint"]  = "Paste the INTEGRATION KEY exactly as issued (\"di\" + base64).",

            // --- AI Integration Helper ---
            ["ai.toggleLabel"]            = "Enable AI Integration Helper",
            ["ai.toggleHelp"]             = "Guides AI coding agents (Claude Code / Codex / Cursor / Cline) to read the SDK's integration knowledge base on session cold-start. Three layers, all gated on per-tool environment signals (presence of `.claude/`, `.cursor/`, `.clinerules`, existing `AGENTS.md` at the project root): (1) KB copy — `<project>/.daro/integration-kb/` mirrors `<package>/Documentation~/`; (2) own-file directives at `.claude/rules/`, `.cursor/rules/`, `.clinerules/` (vendor-owned, never overwrite user files); (3) marker block inject into root `AGENTS.md` (Codex, inject-into-existing-file only). Toggle off cleans everything; legacy root CLAUDE.md marker is swept automatically.",
            ["ai.noAgentEnv"]             = "No AI agent environment detected at the project root (.claude/, .cursor/, .clinerules, AGENTS.md all absent). Nothing to reconcile — open at least one of these tools in this project, or create AGENTS.md, then toggle again.",
            ["ai.clineFileMode"]          = "Cline `.clinerules` exists as a single file at the project root — directory mode is unavailable so Cline integration is skipped. Convert to directory mode manually to enable.",
            ["ai.markerInjected"]         = "Directive applied to AI agent rule paths.",
            ["ai.markerCleaned"]          = "Directive removed from AI agent rule paths.",
            ["v.any.aiKbMarker.msg"]      = "AI integration helper is ON but AGENTS.md is missing the marker block.",
            ["v.any.aiKbMarker.hint"]     = "Re-toggle in Integration Manager, or ensure AGENTS.md exists at the project root.",
            ["v.any.aiKbOwnFile.msg"]     = "AI integration helper is ON but one or more env-signaled own-file rule paths are occupied by user-authored content or stale.",
            ["v.any.aiKbOwnFile.hint"]    = "Manually remove the conflicting file at the target path, or re-toggle in Integration Manager.",
            ["v.any.aiKbKb.msg"]          = "AI integration helper is ON but `.daro/integration-kb/` is user-occupied or stale.",
            ["v.any.aiKbKb.hint"]         = "Manually remove `.daro/integration-kb/`, or re-toggle in Integration Manager.",

            // --- Devtools — LogOverlay ---
            ["devtools.logOverlay.title"]                       = "Log Overlay",
            ["devtools.logOverlay.help"]                        = "Floating, draggable + resizable runtime log panel that filters Daro SDK callbacks (and optional consumer-side structured logs) into source-tagged rows with a detail modal and multi-select filter popup. Import bundles assets at `Assets/Daro Devtools/Log Overlay/`; consumers tweak namespace / log prefix / PlayerPrefs keys post-import as needed.",
            ["btn.importLogOverlay"]                            = "Import Log Overlay",
            ["devtools.logOverlay.status.ready"]                = "Ready to import.",
            ["devtools.logOverlay.status.imported"]             = "Imported at Assets/Daro Devtools/Log Overlay/.",
            ["devtools.logOverlay.notAvailable.packageMissing"] = "Daro Unity SDK package not resolved — open this menu from a project with the SDK linked.",
            ["devtools.logOverlay.notAvailable.assetMissing"]   = "LogOverlay.unitypackage not bundled in this SDK build. Run `Daro / Devtools / Rebuild LogOverlay Package` from the SDK dev environment first.",
        };

        private static readonly Dictionary<string, string> _ko = new Dictionary<string, string>
        {
            // --- window chrome ---
            ["window.title"]              = "Daro Integration Manager",
            ["nosettings.warning"]        = "DaroSettings 에셋을 찾을 수 없습니다. 시작하려면 생성하세요.",
            ["nosettings.create"]         = "Settings 에셋 생성",
            ["foldout.settings"]          = "설정",
            ["foldout.validation"]        = "빌드 검증",
            ["foldout.aiHelper"]          = "AI 통합 헬퍼",
            ["foldout.devtools"]          = "개발자 도구",
            ["section.mediation"]         = "Mediation",
            ["section.ios"]               = "iOS",
            ["section.android"]           = "Android",
            ["field.mediation"]           = "Mediation 변형",
            ["field.integrationKey"]      = "INTEGRATION KEY",
            ["field.attDescription"]      = "ATT 설명",
            ["btn.runChecks"]             = "검사 실행",
            ["btn.validateKey"]           = "키 검증",
            ["validate.idle"]             = "",
            ["validate.ik.ok"]            = "✓ 형식 OK — 실제 검증은 빌드 시점에 so.daro gradle 플러그인이 해제하며 수행합니다.",
            ["validate.ik.empty"]         = "✗ INTEGRATION KEY 가 비어 있습니다.",
            ["validate.ik.legacyAppKey"]  = "✗ 구세대 앱 키(UUID)로 보입니다 — Daro 대시보드에서 INTEGRATION KEY 를 발급받으세요.",
            ["validate.ik.missingPrefix"] = "✗ INTEGRATION KEY 는 \"di\" 로 시작해야 합니다.",
            ["validate.ik.invalidBase64"] = "✗ 페이로드가 유효한 base64 가 아닙니다 — 잘렸거나 변형됐을 가능성이 큽니다.",
            ["validate.ik.tooShort"]      = "✗ 페이로드가 너무 짧습니다 — 잘렸을 가능성이 큽니다.",
            ["assetPath.unsaved"]         = "(저장되지 않음)",
            ["os.windowsWarning"]         = "Windows 환경 — iOS 빌드 검증은 호스트 제한됩니다.",
            ["error.uiAssetsMissing"]     = "Daro Integration Manager — UI 에셋 누락. so.daro.unity 패키지를 다시 임포트하세요.",

            // --- validator messages (msg + hint per CheckId) ---
            ["v.any.settings.msg"]        = "DaroSettings 에셋을 찾을 수 없습니다.",
            ["v.any.settings.hint"]       = "Daro > Integration Manager에서 생성하세요.",
            ["v.any.resourcesPath.msg"]   = "DaroSettings가 Resources/ 아래에 있습니다 ({0}). Editor 필드가 런타임 빌드에 포함됩니다.",
            ["v.any.resourcesPath.hint"]  = "Resources/ 바깥으로 이동하세요.",
            ["v.any.mediation.msg"]       = "Mediation: {0}",
            ["v.any.mediation.hint"]      = "",
            ["v.any.edm4u.msg"]           = "EDM4U가 감지되지 않습니다. 네이티브 의존성이 resolve되지 않을 수 있습니다.",
            ["v.any.edm4u.hint"]          = "com.google.external-dependency-manager를 설치하세요.",
            ["v.ios.integrationKey.msg"]          = "iOS INTEGRATION KEY 가 비어 있습니다 — daro CLI 가 Info.plist 값을 심지 못해 앱이 실행 직후 크래시합니다.",
            ["v.ios.integrationKey.hint"]         = "iosIntegrationKey 를 입력하세요 (Daro 대시보드에서 발급).",
            ["v.ios.integrationKey.migrate.msg"]  = "레거시 iOS 앱 키 / 키 파일 / AdMob ID 가 남아 있습니다 — 통합 SDK 는 이것들을 INTEGRATION KEY 하나로 대체했습니다.",
            ["v.ios.integrationKey.migrate.hint"] = "Daro 대시보드에서 INTEGRATION KEY 를 발급받아 iosIntegrationKey 에 입력하세요. 레거시 필드는 무시됩니다.",
            ["v.ios.integrationKey.format.hint"]  = "발급된 그대로 붙여넣으세요 (\"di\" + base64).",
            ["v.ios.attDescription.msg"]  = "ATT 설명이 비어 있습니다 — App Store에서 바이너리를 거부합니다.",
            ["v.ios.attDescription.hint"] = "attPromptDescription을 입력하세요.",
            ["v.android.integrationKey.msg"]          = "Android INTEGRATION KEY 가 비어 있습니다 — so.daro gradle 플러그인이 소비자 빌드를 실패시킵니다.",
            ["v.android.integrationKey.hint"]         = "androidIntegrationKey 를 입력하세요 (Daro 대시보드에서 발급).",
            ["v.android.integrationKey.migrate.msg"]  = "구세대 Android 앱 키/키 파일이 감지됐습니다 — 통합 SDK 는 INTEGRATION KEY 하나로 대체했습니다.",
            ["v.android.integrationKey.migrate.hint"] = "Daro 대시보드에서 INTEGRATION KEY 를 발급받아 androidIntegrationKey 에 입력하세요. 구 필드는 무시됩니다.",
            ["v.android.integrationKey.format.hint"]  = "발급받은 INTEGRATION KEY 를 그대로 붙여넣으세요 (\"di\" + base64).",

            // --- AI 통합 헬퍼 ---
            ["ai.toggleLabel"]            = "AI 통합 헬퍼 활성화",
            ["ai.toggleHelp"]             = "AI 코딩 에이전트 (Claude Code / Codex / Cursor / Cline) 가 세션 시작 시 SDK 의 integration knowledge base 를 자동 참고하도록 가이드합니다. 3 layer, 모두 per-tool environment signal (프로젝트 루트의 `.claude/`, `.cursor/`, `.clinerules`, 기존 `AGENTS.md` 존재) 로 gate: (1) KB 복사 — `<project>/.daro/integration-kb/` 가 `<package>/Documentation~/` mirror; (2) `.claude/rules/`, `.cursor/rules/`, `.clinerules/` 에 vendor 소유 directive 파일 (사용자 작성 파일은 절대 안 건드림); (3) 루트 `AGENTS.md` 에 marker 블록 inject (Codex 만, 이미 존재하는 파일에만). 토글 off 시 모두 정리, 기존 sprint 의 루트 CLAUDE.md marker 도 자동 sweep.",
            ["ai.noAgentEnv"]             = "프로젝트 루트에서 AI 에이전트 environment 감지 안 됨 (.claude/, .cursor/, .clinerules, AGENTS.md 모두 부재). reconcile 할 게 없습니다 — 이 프로젝트에서 위 도구 중 하나 사용하거나 AGENTS.md 를 생성 후 다시 토글하세요.",
            ["ai.clineFileMode"]          = "Cline `.clinerules` 가 프로젝트 루트에 *단일 파일* 로 존재 — directory mode 사용 불가능. Cline 통합이 skip 됩니다. directory mode 로 수동 마이그레이션하면 사용 가능.",
            ["ai.markerInjected"]         = "AI 에이전트 rule 경로에 directive 를 적용했습니다.",
            ["ai.markerCleaned"]          = "AI 에이전트 rule 경로에서 directive 를 제거했습니다.",
            ["v.any.aiKbMarker.msg"]      = "AI 통합 헬퍼 토글이 ON 이지만 AGENTS.md 에 marker 영역이 없습니다.",
            ["v.any.aiKbMarker.hint"]     = "Integration Manager 에서 토글을 다시 한 번 클릭하거나 프로젝트 루트에 AGENTS.md 가 존재하는지 확인하세요.",
            ["v.any.aiKbOwnFile.msg"]     = "AI 통합 헬퍼 토글이 ON 이지만 env-signal 통과한 own-file 경로 중 하나 이상이 사용자 작성 파일로 점유됐거나 stale 상태입니다.",
            ["v.any.aiKbOwnFile.hint"]    = "해당 경로의 파일을 수동 제거하거나 Integration Manager 에서 토글을 다시 클릭하세요.",
            ["v.any.aiKbKb.msg"]          = "AI 통합 헬퍼 토글이 ON 이지만 `.daro/integration-kb/` 가 사용자 점유 또는 stale 상태입니다.",
            ["v.any.aiKbKb.hint"]         = "`.daro/integration-kb/` 를 수동 제거하거나 Integration Manager 에서 토글을 다시 클릭하세요.",

            // --- 개발자 도구 — LogOverlay ---
            ["devtools.logOverlay.title"]                       = "Log Overlay",
            ["devtools.logOverlay.help"]                        = "런타임 로그 패널 — 드래그/리사이즈 가능한 플로팅 오버레이로 Daro SDK 콜백 (옵션으로 consumer 측 구조화 로그 포함) 을 source 별 배지 row, 디테일 모달, 멀티-셀렉트 필터 popup 으로 분류합니다. Import 시 `Assets/Daro Devtools/Log Overlay/` 에 에셋 배포 — namespace / 로그 prefix / PlayerPrefs 키는 가져온 뒤 직접 수정 가능.",
            ["btn.importLogOverlay"]                            = "Log Overlay 가져오기",
            ["devtools.logOverlay.status.ready"]                = "가져올 준비 완료.",
            ["devtools.logOverlay.status.imported"]             = "Assets/Daro Devtools/Log Overlay/ 에 가져왔습니다.",
            ["devtools.logOverlay.notAvailable.packageMissing"] = "Daro Unity SDK 패키지가 resolve 되지 않았습니다 — SDK 가 링크된 프로젝트에서 이 메뉴를 여세요.",
            ["devtools.logOverlay.notAvailable.assetMissing"]   = "LogOverlay.unitypackage 가 이 SDK 빌드에 번들되지 않았습니다. SDK 개발 환경에서 `Daro / Devtools / Rebuild LogOverlay Package` 를 먼저 실행하세요.",
        };
    }
}
