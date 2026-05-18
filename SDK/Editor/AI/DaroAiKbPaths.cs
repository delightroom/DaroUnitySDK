using System.IO;

namespace Daro.Editor
{
    // Central path constants for the AI Integration Helper. Two purposes:
    //
    //   1. Decouple directive payload from package install method. The KB
    //      content is *copied* into a consumer-project-local directory
    //      (`<project>/.daro/integration-kb/`) so the directive can
    //      reference a stable path regardless of whether the SDK is
    //      installed as an embedded UPM package, OpenUPM scoped registry,
    //      `Library/PackageCache/...`, etc.
    //
    //   2. Avoid string duplication. The directive payload, the KB copier,
    //      the validator, and any tests share the same paths from here.
    //
    // All paths are *consumer Unity project root relative* (= `Application.dataPath` parent).
    internal static class DaroAiKbPaths
    {
        // Vendor-owned KB copy root, relative to consumer project root.
        // Used by Bootstrap reconcile to mirror `<package>/Documentation~/`
        // into the consumer project, and by all directive payloads as the
        // base for index / integration / ad-formats / troubleshooting /
        // api-reference references.
        internal const string KbDirRelative = ".daro/integration-kb";

        // Sentinel file inside the KB copy directory marking it as
        // vendor-owned. Clean refuses to delete a directory without this
        // sentinel so a user-authored directory at `.daro/integration-kb/`
        // stays untouched.
        internal const string KbSentinelFileName = ".daro-owned";

        // Per-tool directive file path (relative). Each is gated by a
        // corresponding environment signal (see EnvSignal* below) — written
        // only when the consumer is actually using that tool.
        internal const string ClaudeDirectiveRelative = ".claude/rules/daro-integration-kb.md";
        internal const string CursorDirectiveRelative = ".cursor/rules/daro-integration-kb.mdc";
        internal const string ClineDirectiveRelative  = ".clinerules/daro-integration-kb.md";

        // Environment-signal directories / files. Presence indicates the
        // consumer is using that tool; absence means we don't touch
        // anything related to it (D8 spirit applied to own-file axis).
        internal const string ClaudeEnvDir       = ".claude";
        internal const string CursorEnvDir       = ".cursor";
        internal const string ClineEnvDirOrFile  = ".clinerules";  // may be file (legacy mode) or directory

        // === Helpers ========================================================

        // Resolves a forward-slash relative path under `projectRoot` to a
        // platform-correct absolute path. Used by every callsite that wants
        // to convert a stored constant (e.g. `OwnFileTarget.RelativePath`)
        // to a real filesystem path.
        internal static string ToAbsolute(string projectRoot, string relative)
            => Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        internal static string KbDirAbsolute(string projectRoot)
            => ToAbsolute(projectRoot, KbDirRelative);

        internal static string KbSentinelAbsolute(string projectRoot)
            => Path.Combine(KbDirAbsolute(projectRoot), KbSentinelFileName);

        // True when the consumer has Claude Code wired into the project
        // (presence of `.claude/` directory at project root).
        internal static bool HasClaudeEnv(string projectRoot)
            => Directory.Exists(Path.Combine(projectRoot, ClaudeEnvDir));

        // True when the consumer has Cursor wired into the project
        // (presence of `.cursor/` directory at project root).
        internal static bool HasCursorEnv(string projectRoot)
            => Directory.Exists(Path.Combine(projectRoot, CursorEnvDir));

        // True when the consumer has Cline wired into the project — either
        // `.clinerules` directory (modern mode) or `.clinerules` single
        // file (legacy mode). The own-file generator only proceeds for
        // directory mode; single-file mode is detected by ConflictGuard
        // and surfaced as a UI notice.
        internal static bool HasClineEnv(string projectRoot)
        {
            var path = Path.Combine(projectRoot, ClineEnvDirOrFile);
            return File.Exists(path) || Directory.Exists(path);
        }
    }
}
