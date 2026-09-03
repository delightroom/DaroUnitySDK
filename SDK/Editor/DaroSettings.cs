using UnityEditor;
using UnityEngine;

namespace Daro.Editor
{
    // v1 ships MAX only. AdMob is reserved for v2 — enum stays single-value
    // until the AdMob native artifacts + build-time toggle infrastructure
    // (DaroDependencies XML split, PluginImporter toggle) are added.
    public enum Mediation
    {
        MAX = 0,
    }

    [CreateAssetMenu(menuName = "Daro/Settings", fileName = "DaroSettings")]
    public sealed class DaroSettings : ScriptableObject
    {
        [Header("Mediation")]
        public Mediation mediation = Mediation.MAX;

        [Header("iOS")]
        // INTEGRATION KEY — Android 와 같은 봉투 한 줄("di" + base64).
        // 빌드타임에 `daro platform-key --inject` 가 봉투를 풀어 Info.plist 에
        // 네 값을 심는다: GADApplicationIdentifier / DaroAppKey /
        // DaroAppLovinSdkKey / DaroAppLovinAdReviewKey. 래퍼는 문자열을
        // 배달만 한다 — Editor 는 복호화하지 않는다(시크릿은 네이티브 도구에만).
        public string iosIntegrationKey;

        [TextArea(2, 4)]
        public string attPromptDescription;

        // Legacy iOS 입력 (통합 이전 세대). 마이그레이션 감지용으로만
        // 직렬화를 유지한다 — 주입에는 절대 쓰지 않는다.
        // AdMob app ID 는 이제 봉투가 실어 오므로 별도 입력이 없다.
        [HideInInspector] public string iosDaroAppKey;
        [HideInInspector] public TextAsset iosKeyFile;     // ios-daro-key.txt
        [HideInInspector] public string iosAdMobAppId;

        [Header("Android")]
        // INTEGRATION KEY — one envelope string per platform, issued by the
        // Daro dashboard ("di" + base64). The so.daro gradle plugin decrypts
        // it at build time and fills the DARO_APP_KEY / ADMOB_ID /
        // APPLOVIN_KEY manifest placeholders. The wrapper only delivers the
        // string — the Editor never decrypts (the secret lives only in the
        // native tools).
        public string androidIntegrationKey;

        // Legacy key pair (pre-unified generation, daro-m 1.3.x). Kept
        // serialized so the validator can detect an un-migrated settings
        // asset and fail the build with migration guidance — never read
        // these for injection.
        [HideInInspector] public string androidDaroAppKey;
        [HideInInspector] public TextAsset androidKeyFile;    // android-daro-key.txt

        [Header("Editor Mock")]
        public Daro.DaroEditorSettings editorMock;  // created by another agent in Daro.Runtime

        [Header("AI Assistant")]
        // Toggling on guides AI coding agents (Claude Code / Codex / Cursor /
        // Cline) to read the SDK's integration knowledge base on session
        // cold-start. Three-layer reconcile, all env-signal gated — see
        // DaroAiKbBootstrap + SDK/Editor/AI/:
        //   - KB copy: mirrors `<package>/Documentation~/` into
        //     `<project>/.daro/integration-kb/` so directive paths stay
        //     stable across UPM install methods.
        //   - Own-file directives: vendor-owned files at `.claude/rules/`,
        //     `.cursor/rules/`, `.clinerules/` — written only when the tool's
        //     env signal (parent indicator dir) is present.
        //   - Marker inject: marker-wrapped directive block into root
        //     `AGENTS.md` (Codex; inject-into-existing-file only).
        // Off cleans everything. Legacy root CLAUDE.md marker is swept
        // automatically.
        public bool enableAiIntegrationHelper;
    }
}
