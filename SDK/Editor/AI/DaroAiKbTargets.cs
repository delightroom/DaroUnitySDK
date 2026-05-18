using System.Collections.Generic;
using System.IO;

namespace Daro.Editor
{
    // Two-axis target registry for the AI Integration Helper.
    //
    // Background: cold-start auto-load mechanisms differ across AI coding
    // tools. The original `integration-knowledge-base` sprint treated all
    // targets uniformly (marker-block inject into the user's main instruction
    // file). A later mechanism fact-check (see
    // `docs/dev/ai-kb-wrapper-expansion/goal.md`) revealed 3 of 4 tools
    // support sub-path *own-file* discovery — letting us deposit a
    // vendor-namespaced file without touching the user's main instruction
    // file. Cleaner trust boundary; we pivot to a hybrid model.
    //
    // Axis A — Own-file (preferred where supported):
    //   - Claude Code  → `.claude/rules/daro-integration-kb.md`     (directory auto-load)
    //   - Cursor       → `.cursor/rules/daro-integration-kb.mdc`    (`.mdc` + frontmatter `alwaysApply: true`)
    //   - Cline        → `.clinerules/daro-integration-kb.md`       (directory mode — file-mode conflict guarded)
    //
    // Axis B — Marker inject (only where own-file isn't an option):
    //   - Codex CLI    → `<project>/AGENTS.md`                       (root single-file auto-load; no sub-path / directory mechanism)
    //
    // Legacy clean: the prior sprint's root `<project>/CLAUDE.md` marker
    // inject is deprecated. Bootstrap's reconcile sweep removes the marker
    // block from any pre-existing `CLAUDE.md` so users who upgrade get clean
    // state automatically.
    //
    // Discovery / creation policy:
    //   - MarkerTargets are inject-into-existing-file only (D8: never
    //     auto-create the user's main instruction file).
    //   - OwnFileTargets are vendor-namespaced paths the SDK owns; both the
    //     parent directory and the file itself are auto-created. Clean only
    //     removes the file when its vendor-ownership header marker is
    //     present (so the SDK never deletes a user-authored file that
    //     happens to share the path).
    //
    // Scope: everything is rooted at the consumer Unity project root
    // (= `Application.dataPath` parent). User-global config (e.g.
    // `~/.claude/CLAUDE.md`) is NOT touched.
    internal static class DaroAiKbTargets
    {
        // === Marker-inject axis (Codex AGENTS.md) ============================

        internal readonly struct MarkerTarget
        {
            internal string FileName { get; }
            internal string Label    { get; }
            internal MarkerTarget(string fileName, string label) { FileName = fileName; Label = label; }
        }

        internal static readonly MarkerTarget[] MarkerTargets = new[]
        {
            new MarkerTarget("AGENTS.md", "Codex / AGENTS.md"),
        };

        // Legacy marker targets — Bootstrap's orphan-clean sweep removes any
        // stale marker block from these on Editor boot so users who upgrade
        // from a prior SDK version (which marker-injected CLAUDE.md) end up
        // in clean state automatically. Never injected into; only swept for
        // Clean.
        internal static readonly string[] LegacyMarkerFileNames = new[]
        {
            "CLAUDE.md",
        };

        internal static IEnumerable<string> MarkerAllPaths()
        {
            var root = DaroProjectRoot.Path;
            foreach (var t in MarkerTargets)
                yield return Path.Combine(root, t.FileName);
        }

        internal static IEnumerable<string> MarkerExistingPaths()
        {
            foreach (var path in MarkerAllPaths())
                if (File.Exists(path)) yield return path;
        }

        internal static IEnumerable<string> LegacyMarkerPaths()
        {
            var root = DaroProjectRoot.Path;
            foreach (var name in LegacyMarkerFileNames)
                yield return Path.Combine(root, name);
        }

        // === Own-file axis (Claude Code / Cursor / Cline) ====================

        internal readonly struct OwnFileTarget
        {
            // Relative path under consumer project root (forward-slash form).
            internal string RelativePath { get; }
            // Display label in Manager UI.
            internal string Label { get; }
            // Tool-specific body produced from the canonical directive block.
            internal System.Func<string, string> BodyComposer { get; }
            // Returns true if the consumer is using this tool — i.e. the
            // tool's parent indicator (e.g. `.claude/` directory) is present.
            // Apply is gated on this; absent signal means we do nothing for
            // this target (D8 spirit applied to own-file axis: don't write
            // into an environment the consumer isn't using).
            internal System.Func<string, bool> EnvSignal { get; }
            // When non-null, returns a skip reason if a conflict prevents
            // the SDK from owning this path (e.g. Cline `.clinerules`
            // existing as a single file rather than a directory). Returning
            // null means no conflict — Apply may proceed.
            internal System.Func<string, string> ConflictGuard { get; }

            internal OwnFileTarget(
                string relativePath,
                string label,
                System.Func<string, string> bodyComposer,
                System.Func<string, bool> envSignal,
                System.Func<string, string> conflictGuard)
            {
                RelativePath  = relativePath;
                Label         = label;
                BodyComposer  = bodyComposer;
                EnvSignal     = envSignal;
                ConflictGuard = conflictGuard;
            }

            internal string AbsolutePath =>
                DaroAiKbPaths.ToAbsolute(DaroProjectRoot.Path, RelativePath);
        }

        internal static readonly OwnFileTarget[] OwnFileTargets = new[]
        {
            new OwnFileTarget(
                relativePath:  DaroAiKbPaths.ClaudeDirectiveRelative,
                label:         "Claude Code (.claude/rules/)",
                bodyComposer:  DaroAiKbOwnFileBodies.ComposeClaude,
                envSignal:     DaroAiKbPaths.HasClaudeEnv,
                conflictGuard: null),

            new OwnFileTarget(
                relativePath:  DaroAiKbPaths.CursorDirectiveRelative,
                label:         "Cursor (.cursor/rules/)",
                bodyComposer:  DaroAiKbOwnFileBodies.ComposeCursorMdc,
                envSignal:     DaroAiKbPaths.HasCursorEnv,
                conflictGuard: null),

            new OwnFileTarget(
                relativePath:  DaroAiKbPaths.ClineDirectiveRelative,
                label:         "Cline (.clinerules/)",
                bodyComposer:  DaroAiKbOwnFileBodies.ComposeCline,
                envSignal:     DaroAiKbPaths.HasClineEnv,
                conflictGuard: ClineFileModeConflict),
        };

        // True when at least one target — marker or own-file — has its
        // environment signal present. Used by Bootstrap to decide whether
        // the KB copy is even worth making (no agent → no consumers → no
        // copy).
        internal static bool AnyEnvSignal()
        {
            var root = DaroProjectRoot.Path;
            foreach (var _ in MarkerExistingPaths()) return true;
            foreach (var t in OwnFileTargets)
                if (t.EnvSignal != null && t.EnvSignal(root)) return true;
            return false;
        }

        // Cline supports either `.clinerules` as a single file OR as a
        // directory. The two modes can't co-exist. If the consumer is
        // already using single-file mode, we refuse to create the directory
        // — surfaces a skip notice instead. Returns null when no conflict
        // (i.e. `.clinerules` is absent or already a directory).
        private static string ClineFileModeConflict(string projectRoot)
        {
            var clinerulesPath = Path.Combine(projectRoot, ".clinerules");
            if (File.Exists(clinerulesPath) && !Directory.Exists(clinerulesPath))
                return "`.clinerules` exists as a single file — directory mode unavailable.";
            return null;
        }
    }
}
