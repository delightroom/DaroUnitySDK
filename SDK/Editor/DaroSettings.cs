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
        public string iosDaroAppKey;
        public TextAsset iosKeyFile;        // ios-daro-key.txt (.txt → TextAsset, NOT DefaultAsset)
        // AdMob app ID — pasted from AdMob console (`ca-app-pub-XXXX~XXXX`).
        // Injected as Info.plist key `GADApplicationIdentifier`. REQUIRED on
        // iOS even for MAX mediation: daro-m has the AppLovin google-adapter
        // transitively, which links the GoogleMobileAds framework — Apple
        // crashes the app at launch if Info.plist lacks this key.
        // Daro 대시보드 표기 = "AdMob Key".
        public string iosAdMobAppId;
        [TextArea(2, 4)]
        public string attPromptDescription;

        [Header("Android")]
        public string androidDaroAppKey;
        public TextAsset androidKeyFile;    // android-daro-key.txt

        [Header("Editor Mock")]
        public Daro.DaroEditorSettings editorMock;  // created by another agent in Daro.Runtime

        [Header("AI Assistant")]
        // Toggling on writes a marker-wrapped pointer line to the consumer project's
        // CLAUDE.md so AI coding agents (Claude Code etc.) auto-discover the SDK's
        // Documentation~/ knowledge base on session cold-start. Off cleans the marker
        // block, leaves the rest of CLAUDE.md untouched. See SDK/Editor/AI/.
        public bool enableAiIntegrationHelper;
    }
}
