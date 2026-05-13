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
            ["foldout.nativeDeps"]        = "Native Dependencies",
            ["foldout.validation"]        = "Build Validation",
            ["foldout.aiHelper"]          = "AI Integration Helper",
            ["section.mediation"]         = "Mediation",
            ["section.ios"]               = "iOS",
            ["section.android"]           = "Android",
            ["field.mediation"]           = "Mediation Variant",
            ["field.daroAppKey"]          = "Daro App Key",
            ["field.keyFile"]             = "Key File",
            ["field.adMobKey"]            = "AdMob Key",
            ["field.attDescription"]      = "ATT Description",
            ["btn.resolveAndroid"]        = "Android Force Resolve",
            ["btn.resolveIos"]            = "iOS Force Resolve",
            ["btn.runChecks"]             = "Run Checks",
            ["btn.validate"]              = "Validate Key Pair",
            ["validate.idle"]             = "",
            ["validate.valid"]            = "✓ Valid — daroAppKey + keyfile pair decrypts cleanly.",
            ["validate.tagMismatch"]      = "✗ Tag mismatch — daroAppKey and keyfile are NOT a matching pair (different issuance / typo / wrong env).",
            ["validate.emptyAppKey"]      = "✗ daroAppKey is empty.",
            ["validate.noKeyfile"]        = "✗ Keyfile not assigned.",
            ["validate.invalidBase64"]    = "✗ Keyfile is not valid Base64.",
            ["validate.tooShort"]         = "✗ Keyfile is too short to be valid (less than 28 bytes after Base64 decode).",
            ["assetPath.unsaved"]         = "(unsaved)",
            ["os.windowsWarning"]         = "Running on Windows — iOS build validation is host-limited.",
            ["os.iosResolveTooltip"]      = "iOS CocoaPods resolution requires macOS.",
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
            ["v.ios.daroAppKey.msg"]      = "iOS Daro App Key empty — native fatalError at launch.",
            ["v.ios.daroAppKey.hint"]     = "Enter iosDaroAppKey.",
            ["v.ios.keyFile.msg"]         = "ios-daro-key.txt not assigned.",
            ["v.ios.keyFile.hint"]        = "Assign in DaroSettings > iOS Key File.",
            ["v.ios.attDescription.msg"]  = "ATT description empty — App Store will reject binary.",
            ["v.ios.attDescription.hint"] = "Enter attPromptDescription.",
            ["v.ios.admobAppId.msg"]      = "AdMob Key empty — GADApplicationIdentifier missing will crash the app at launch.",
            ["v.ios.admobAppId.hint"]     = "Enter the AdMob App ID (AdMob console → App Settings → App ID).",
            ["v.android.daroAppKey.msg"]  = "Android Daro App Key empty — init fails with daroErrorCode=-3.",
            ["v.android.daroAppKey.hint"] = "Enter androidDaroAppKey.",
            ["v.android.keyFile.msg"]     = "android-daro-key.txt not assigned.",
            ["v.android.keyFile.hint"]    = "Assign in DaroSettings > Android Key File.",

            // --- AI Integration Helper ---
            ["ai.toggleLabel"]            = "Enable AI Integration Helper",
            ["ai.toggleHelp"]             = "Injects a directive block into agent-instruction files at the project root (CLAUDE.md for Claude Code, AGENTS.md for Codex) so AI coding agents are guided to read the SDK's integration knowledge base on session cold-start. Both files are detected automatically — only those that already exist are written to. Toggling off cleans the marker block; bytes outside it are preserved byte-for-byte.",
            ["ai.noAgentFile"]            = "No CLAUDE.md or AGENTS.md found at the project root. Create at least one (the helper does not auto-create files) and toggle again.",
            ["ai.markerInjected"]         = "Directive injected into agent-instruction file(s).",
            ["ai.markerCleaned"]          = "Directive removed from agent-instruction file(s).",
            ["v.any.aiKbMarker.msg"]      = "AI integration helper is ON but at least one agent-instruction file (CLAUDE.md / AGENTS.md) is missing the marker block.",
            ["v.any.aiKbMarker.hint"]     = "Re-toggle in Integration Manager, or ensure CLAUDE.md / AGENTS.md exists at the project root.",
        };

        private static readonly Dictionary<string, string> _ko = new Dictionary<string, string>
        {
            // --- window chrome ---
            ["window.title"]              = "Daro Integration Manager",
            ["nosettings.warning"]        = "DaroSettings 에셋을 찾을 수 없습니다. 시작하려면 생성하세요.",
            ["nosettings.create"]         = "Settings 에셋 생성",
            ["foldout.settings"]          = "설정",
            ["foldout.nativeDeps"]        = "네이티브 의존성",
            ["foldout.validation"]        = "빌드 검증",
            ["foldout.aiHelper"]          = "AI 통합 헬퍼",
            ["section.mediation"]         = "Mediation",
            ["section.ios"]               = "iOS",
            ["section.android"]           = "Android",
            ["field.mediation"]           = "Mediation 변형",
            ["field.daroAppKey"]          = "Daro 앱 키",
            ["field.keyFile"]             = "키 파일",
            ["field.adMobKey"]            = "AdMob 키",
            ["field.attDescription"]      = "ATT 설명",
            ["btn.resolveAndroid"]        = "Android 의존성 강제 Resolve",
            ["btn.resolveIos"]            = "iOS 의존성 강제 Resolve",
            ["btn.runChecks"]             = "검사 실행",
            ["btn.validate"]              = "키 페어 검증",
            ["validate.idle"]             = "",
            ["validate.valid"]            = "✓ 유효 — daroAppKey 와 keyfile 페어가 정상 복호화됩니다.",
            ["validate.tagMismatch"]      = "✗ Tag mismatch — daroAppKey 와 keyfile 이 같은 페어가 아닙니다 (다른 발급 / 오타 / 환경 불일치).",
            ["validate.emptyAppKey"]      = "✗ daroAppKey 가 비어있습니다.",
            ["validate.noKeyfile"]        = "✗ Keyfile 이 할당되지 않았습니다.",
            ["validate.invalidBase64"]    = "✗ Keyfile 이 유효한 Base64 형식이 아닙니다.",
            ["validate.tooShort"]         = "✗ Keyfile 이 너무 짧습니다 (Base64 디코드 후 28 byte 미만).",
            ["assetPath.unsaved"]         = "(저장되지 않음)",
            ["os.windowsWarning"]         = "Windows 환경 — iOS 빌드 검증은 호스트 제한됩니다.",
            ["os.iosResolveTooltip"]      = "iOS CocoaPods Resolve는 macOS에서만 동작합니다.",
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
            ["v.ios.daroAppKey.msg"]      = "iOS Daro 앱 키가 비어 있습니다 — 실행 시 native fatalError가 발생합니다.",
            ["v.ios.daroAppKey.hint"]     = "iosDaroAppKey를 입력하세요.",
            ["v.ios.keyFile.msg"]         = "ios-daro-key.txt가 할당되지 않았습니다.",
            ["v.ios.keyFile.hint"]        = "DaroSettings > iOS Key File에 할당하세요.",
            ["v.ios.attDescription.msg"]  = "ATT 설명이 비어 있습니다 — App Store에서 바이너리를 거부합니다.",
            ["v.ios.attDescription.hint"] = "attPromptDescription을 입력하세요.",
            ["v.ios.admobAppId.msg"]      = "AdMob Key가 비어 있습니다 — GADApplicationIdentifier 누락 시 앱이 실행 직후 크래시합니다.",
            ["v.ios.admobAppId.hint"]     = "AdMob 콘솔 (App Settings → App ID) 에서 발급받은 ID를 입력하세요.",
            ["v.android.daroAppKey.msg"]  = "Android Daro 앱 키가 비어 있습니다 — daroErrorCode=-3으로 init이 실패합니다.",
            ["v.android.daroAppKey.hint"] = "androidDaroAppKey를 입력하세요.",
            ["v.android.keyFile.msg"]     = "android-daro-key.txt가 할당되지 않았습니다.",
            ["v.android.keyFile.hint"]    = "DaroSettings > Android Key File에 할당하세요.",

            // --- AI 통합 헬퍼 ---
            ["ai.toggleLabel"]            = "AI 통합 헬퍼 활성화",
            ["ai.toggleHelp"]             = "프로젝트 루트의 agent-instruction 파일 (Claude Code 용 CLAUDE.md, Codex 용 AGENTS.md) 에 directive 블록을 inject 해서 AI 코딩 에이전트가 세션 시작 시 SDK 의 integration knowledge base 를 자동 참고하도록 유도합니다. 두 파일을 자동 감지 — 존재하는 파일에만 inject. 토글 off 시 marker 영역만 정리, 그 외 내용은 byte-for-byte 보존.",
            ["ai.noAgentFile"]            = "프로젝트 루트에 CLAUDE.md / AGENTS.md 둘 다 없습니다. 최소 한 개 직접 생성 후 다시 토글하세요 — 헬퍼는 파일을 자동 생성하지 않습니다.",
            ["ai.markerInjected"]         = "agent-instruction 파일에 directive 를 inject 했습니다.",
            ["ai.markerCleaned"]          = "agent-instruction 파일에서 directive 를 제거했습니다.",
            ["v.any.aiKbMarker.msg"]      = "AI 통합 헬퍼 토글이 ON 이지만 agent-instruction 파일 (CLAUDE.md / AGENTS.md) 중 하나 이상에 marker 영역이 없습니다.",
            ["v.any.aiKbMarker.hint"]     = "Integration Manager 에서 토글을 다시 한 번 클릭하거나 프로젝트 루트의 CLAUDE.md / AGENTS.md 존재 여부를 확인하세요.",
        };
    }
}
