using System.IO;
using Daro.Internal;

namespace Daro.Editor
{
    // Whole-file vendor-owned writer for the own-file axis of the AI
    // Integration Helper (see DaroAiKbTargets). Parallel to
    // DaroAiKbInjector — same Apply / Clean / introspection contract — but
    // operates on file-level ownership rather than marker-block ownership.
    //
    // Ownership model:
    //   - The SDK writes the entire file; content is canonical per the
    //     target's BodyComposer.
    //   - An ownership marker (`OwnerMarker`) sits on a dedicated line so
    //     `IsOwned` / `Clean` can tell vendor-owned files apart from a
    //     file a user happened to author at the same path.
    //   - Apply auto-creates parent directories and the file itself
    //     (vendor-namespaced paths only — `.claude/rules/`,
    //     `.cursor/rules/`, `.clinerules/`).
    //   - Apply with byte-identical content is a NoOp (idempotent).
    //   - Clean deletes the file only when the ownership marker is present
    //     (so the SDK never deletes a user-authored file under the same
    //     path).
    internal static class DaroAiKbOwnFileWriter
    {
        // Sentinel string carried verbatim in every vendor-written file.
        // The `v1` suffix lets future schema changes detect-and-rewrite
        // older bodies without confusing them with hand-edited user files.
        internal const string OwnerMarker = "<!-- daro:integration-kb owned-file v1 -->";

        internal enum ApplyResult
        {
            // Conflict guard refused (e.g. Cline `.clinerules` is a single
            // file). Caller surfaces a notice; no file written.
            ConflictSkipped,
            // File didn't exist; created.
            Created,
            // File existed and was vendor-owned; content differed; rewritten.
            Updated,
            // File existed, vendor-owned, byte-identical → no write.
            NoOp,
            // File existed but is NOT vendor-owned (no OwnerMarker) — Apply
            // refuses to overwrite. Surfaces as a Validator Warn / UI notice
            // so the user can resolve manually.
            UserOwnedSkipped,
        }

        // Writes `body` (composed via target.BodyComposer) to
        // `absolutePath`. Parent dirs auto-created. Returns the disposition.
        internal static ApplyResult Apply(string absolutePath, string body, string conflictReason)
        {
            if (!string.IsNullOrEmpty(conflictReason))
            {
                DaroLog.Warn("Editor", $"[AI KB] Apply conflict skip → {absolutePath} — {conflictReason}");
                return ApplyResult.ConflictSkipped;
            }

            if (File.Exists(absolutePath))
            {
                var existing = File.ReadAllText(absolutePath);
                if (!IsOwnedContent(existing))
                {
                    DaroLog.Warn("Editor", $"[AI KB] Apply user-owned skip → {absolutePath} (no ownership marker)");
                    return ApplyResult.UserOwnedSkipped;
                }
                if (existing == body)
                {
                    return ApplyResult.NoOp;
                }
                File.WriteAllText(absolutePath, body);
                DaroLog.Info("Editor", $"[AI KB] Updated → {absolutePath}");
                return ApplyResult.Updated;
            }

            var parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllText(absolutePath, body);
            DaroLog.Info("Editor", $"[AI KB] Created → {absolutePath}");
            return ApplyResult.Created;
        }

        // Removes the file if vendor-owned. Returns true if a file was
        // deleted, false if no file or the file is user-owned.
        internal static bool Clean(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return false;

            var existing = File.ReadAllText(absolutePath);
            if (!IsOwnedContent(existing))
                return false;

            File.Delete(absolutePath);
            DaroLog.Info("Editor", $"[AI KB] Clean → {absolutePath}");
            return true;
        }

        // True when the file at `absolutePath` exists and carries the
        // vendor-ownership marker. Used by Validator + UI status.
        internal static bool IsOwned(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return false;
            return IsOwnedContent(File.ReadAllText(absolutePath));
        }

        // True when the body's current content matches what Apply would
        // produce for the given target (i.e. up-to-date / NoOp would
        // result). Used by Validator to detect stale vendor-owned files
        // after a payload schema bump.
        internal static bool IsUpToDate(string absolutePath, string expectedBody)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return false;
            return File.ReadAllText(absolutePath) == expectedBody;
        }

        private static bool IsOwnedContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            // Cheap substring check — OwnerMarker is unique enough that a
            // user-authored file is extremely unlikely to contain it by
            // accident. Anchored to the file content, not a specific
            // position, so Cursor's frontmatter prefix doesn't shift the
            // detection.
            return content.IndexOf(OwnerMarker, System.StringComparison.Ordinal) >= 0;
        }
    }
}
