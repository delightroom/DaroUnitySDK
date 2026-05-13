using System.Collections.Generic;
using System.IO;

namespace Daro.Editor
{
    // Set of agent-instruction files the AI Integration Helper may write
    // into. Two supported in v0:
    //   - CLAUDE.md  — Claude Code (Anthropic)
    //   - AGENTS.md  — Codex (OpenAI) / multiple other agents that have
    //                  adopted the AGENTS.md convention.
    //
    // Discovery policy: only files that already exist at the consumer
    // project root get injected. The helper does NOT auto-create either
    // file (D8) — a missing file is surfaced via UI notice + Validator
    // Warn instead. This keeps the helper non-invasive: developers who
    // don't use AI agents see nothing they didn't opt into.
    //
    // Cleanup policy: Clean runs unconditionally over both paths (Clean
    // no-ops on missing files), so toggling OFF or removing the SDK
    // package never leaves an orphan marker behind, regardless of which
    // target the marker was originally written to.
    internal static class DaroAiKbTargets
    {
        internal readonly struct Target
        {
            internal string FileName { get; }
            internal string Label    { get; }
            internal Target(string fileName, string label) { FileName = fileName; Label = label; }
        }

        internal static readonly Target[] All = new[]
        {
            new Target("CLAUDE.md", "Claude Code"),
            new Target("AGENTS.md", "Codex / AGENTS.md"),
        };

        // All candidate absolute paths (existing or not). Used by Clean
        // and by orphan reconcile so we cover both files defensively.
        internal static IEnumerable<string> AllPaths()
        {
            var root = DaroProjectRoot.Path;
            foreach (var t in All)
                yield return Path.Combine(root, t.FileName);
        }

        // Only the absolute paths of files that already exist. Apply
        // iterates this so we never auto-create a file.
        internal static IEnumerable<string> ExistingPaths()
        {
            foreach (var path in AllPaths())
                if (File.Exists(path)) yield return path;
        }

        // True iff at least one target file exists on disk. Drives the
        // "no agent-instruction file found" UI notice when the toggle is on
        // but neither CLAUDE.md nor AGENTS.md is present.
        internal static bool AnyExists()
        {
            foreach (var _ in ExistingPaths()) return true;
            return false;
        }
    }
}
