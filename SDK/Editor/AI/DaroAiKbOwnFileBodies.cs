namespace Daro.Editor
{
    // Per-tool body composers for the own-file targets. Each takes the
    // canonical directive payload (`DaroAiKbPayload.DirectiveBlock`) and
    // wraps it in whatever shape the tool's rule loader expects.
    //
    // All bodies carry `DaroAiKbOwnFileWriter.OwnerMarker` on a dedicated
    // line so Clean can identify which files the SDK owns vs files a user
    // happened to write at the same path.
    internal static class DaroAiKbOwnFileBodies
    {
        // Plain markdown — works for Claude Code's `.claude/rules/*.md` and
        // Cline's `.clinerules/*.md` directory loaders. The ownership marker
        // is the file's first non-blank line. Both tools accept the same
        // shape today; keep them named separately in DaroAiKbTargets so a
        // future divergence (different frontmatter, different separator,
        // etc.) is a one-function change here.
        internal static string ComposeClaude(string payload) => ComposePlainMarkdown(payload);
        internal static string ComposeCline(string payload)  => ComposePlainMarkdown(payload);

        // Cursor `.mdc` — YAML frontmatter controls loader behavior;
        // `alwaysApply: true` injects the rule on every session start
        // regardless of glob match. Body follows after the closing `---`.
        internal static string ComposeCursorMdc(string payload)
        {
            const string frontmatter =
                "---\n" +
                "description: Daro Ad SDK integration knowledge base — read before any ad-related code\n" +
                "alwaysApply: true\n" +
                "---\n";
            return frontmatter + DaroAiKbOwnFileWriter.OwnerMarker + "\n\n" + (payload ?? string.Empty) + "\n";
        }

        private static string ComposePlainMarkdown(string payload)
            => DaroAiKbOwnFileWriter.OwnerMarker + "\n\n" + (payload ?? string.Empty) + "\n";
    }
}
