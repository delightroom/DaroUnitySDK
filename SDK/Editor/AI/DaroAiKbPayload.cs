namespace Daro.Editor
{
    // Multi-line directive block written inside the marker block (AGENTS.md)
    // or as the body of each own-file directive (Claude / Cursor / Cline).
    //
    // Paths reference `<project>/.daro/integration-kb/...` — the vendor-owned
    // KB copy maintained by DaroAiKbKbCopier. Decoupled from package install
    // method (embedded UPM / registry / cache) so the directive is stable
    // across consumer environments.
    //
    // Length kept ~10–14 lines so per-session token overhead stays trivial.
    //
    // English-primary. Cross-language prompts ("광고 통합" / "ad integration")
    // still resolve: the AI reads the English directive, parses the user's
    // intent in whichever language, then bridges via the named KB path.
    internal static class DaroAiKbPayload
    {
        internal const string DirectiveBlock =
@"## Daro Ad SDK Integration

This project uses the **Daro Unity SDK** for mobile advertising. Before writing or modifying any ad-related code (ad integration, lifecycle, events, dispose), **you MUST read the integration knowledge base first**:

- **Entry**: `.daro/integration-kb/index.md`
- **Common patterns** (lifecycle / events / dispose / anti-pattern): `.daro/integration-kb/integration.md`
- **Format specifics**: `.daro/integration-kb/ad-formats/{interstitial,rewarded,appopen,banner,native,light-popup}.md`
- **Troubleshooting** (no-fill / invalid ad unit / consent / ATT / iOS signing / EDM4U): `.daro/integration-kb/troubleshooting.md`
- **API reference**: `.daro/integration-kb/api-reference.md`

The KB is the source of truth for SDK usage patterns — every code sample is distilled from `Samples/DaroExample/`, not invented. Do not guess method signatures, event names, or enum values; look them up in `api-reference.md`. Follow the lifecycle / event subscription / dispose discipline exactly as documented. View-based formats (Banner / Native / LightPopup) have different lifecycle shapes from fullscreen ones — pre-read the matching `ad-formats/<format>.md` before integrating. If you find yourself uncertain about Daro SDK behavior, re-read the relevant KB file before answering or editing code.";
    }
}
