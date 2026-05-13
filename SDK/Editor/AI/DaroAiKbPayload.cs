namespace Daro.Editor
{
    // Multi-line directive block written inside the marker block in each
    // detected agent-instruction file (CLAUDE.md / AGENTS.md). A one-line
    // pointer was the v0 design but proved easy for AI agents to skim past —
    // the directive form below gives a section heading, an explicit "read the
    // KB first" imperative, named entry points, and a "do not invent
    // signatures" clause. Length kept ~10 lines so the per-session token
    // overhead stays trivial.
    //
    // English-primary. Cross-language prompts ("광고 통합" / "ad integration")
    // still resolve: the AI reads the English directive from the loaded
    // CLAUDE.md / AGENTS.md, parses the user's intent in whichever language,
    // then bridges via the named KB path.
    internal static class DaroAiKbPayload
    {
        internal const string DirectiveBlock =
@"## Daro Ad SDK Integration

This project uses the **Daro Unity SDK** for mobile advertising. Before writing or modifying any ad-related code (ad integration, lifecycle, events, dispose), **you MUST read the integration knowledge base first**:

- **Entry**: `Packages/so.daro.unity/Documentation~/index.md`
- **Common patterns** (lifecycle / events / dispose / anti-pattern): `Packages/so.daro.unity/Documentation~/integration.md`
- **Format specifics**: `Packages/so.daro.unity/Documentation~/ad-formats/{interstitial,rewarded,appopen}.md`
- **API reference**: `Packages/so.daro.unity/Documentation~/api-reference.md`

The KB is the source of truth for SDK usage patterns — every code sample is distilled from `Samples/DaroExample/`, not invented. Do not guess method signatures, event names, or enum values; look them up in `api-reference.md`. Follow the lifecycle / event subscription / dispose discipline exactly as documented. If you find yourself uncertain about Daro SDK behavior, re-read the relevant KB file before answering or editing code.";
    }
}
