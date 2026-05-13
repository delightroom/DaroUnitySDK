using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Daro.Internal;

namespace Daro.Editor
{
    // Marker-based idempotent text editor for the AI integration helper's
    // pointer line in consumer `CLAUDE.md`. Mirrors the wrap/regex pattern
    // from DaroAndroidPostProcessor (HTML comment markers instead of `//`
    // line comments since CLAUDE.md is markdown).
    //
    // Marker scheme (vendor-scoped, conflict-free if other SDKs adopt the
    // same pattern under their own prefix):
    //
    //     <!-- daro:integration-kb start -->
    //     ...payload (single line or multi-line)...
    //     <!-- daro:integration-kb end -->
    //
    // Contract:
    // - `Apply` is **idempotent**: calling it twice with the same payload
    //   produces byte-identical file content.
    // - The marker block is the **only** region this class touches. Bytes
    //   outside the block (including blank lines, headings, the user's own
    //   notes) are preserved byte-for-byte.
    // - `Apply` does not create CLAUDE.md if missing — see D8. Surface the
    //   missing-file state to the UI instead so the user opts into creating
    //   it themselves.
    // - Line endings are preserved (CRLF / LF detected from the first
    //   occurrence in the existing file; new-file path is unreachable since
    //   we don't create).
    internal static class DaroAiKbInjector
    {
        internal const string MarkerArea = "integration-kb";
        internal const string MarkerBegin = "<!-- daro:" + MarkerArea + " start -->";
        internal const string MarkerEnd   = "<!-- daro:" + MarkerArea + " end -->";

        // Matches one full marker block, optionally followed by a single
        // trailing newline so a clean removal collapses to one boundary
        // rather than leaving a stray blank line. `Singleline` so `.` spans
        // newlines inside the payload.
        private static readonly Regex BlockRegex = new Regex(
            Regex.Escape(MarkerBegin) + ".*?" + Regex.Escape(MarkerEnd) + @"(\r?\n)?",
            RegexOptions.Singleline);

        internal enum ApplyResult
        {
            // File didn't exist — caller should surface a "create CLAUDE.md first" notice.
            FileMissing,
            // No marker block previously present; one was appended.
            Injected,
            // Marker block existed with different content; replaced in place.
            Updated,
            // Marker block existed with byte-identical content; file unchanged.
            NoOp,
        }

        // Writes a marker-wrapped block containing `payload` into `filePath`.
        // Does not create the file. Preserves byte content outside the block.
        internal static ApplyResult Apply(string filePath, string payload)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                DaroLog.Warn("Editor", $"[AI KB] Apply: target file missing — {filePath}");
                return ApplyResult.FileMissing;
            }

            var original = File.ReadAllText(filePath);
            var newline = DetectLineEnding(original);
            var block = BuildBlock(payload, newline);

            var match = BlockRegex.Match(original);
            string updated;
            ApplyResult result;

            if (match.Success)
            {
                // Existing block — replace in place. Preserve trailing newline
                // captured by the regex if any (the new block contributes its own).
                updated = original.Substring(0, match.Index)
                    + block
                    + original.Substring(match.Index + match.Length);
                result = ApplyResult.Updated;
            }
            else
            {
                // No marker — append. Ensure a single blank line separates the
                // previous content from the block (when prior content exists).
                var sb = new StringBuilder(original);
                if (sb.Length > 0)
                {
                    if (!EndsWithNewline(original)) sb.Append(newline);
                    // Blank-line separator before our block (only if there is
                    // prior non-empty content).
                    sb.Append(newline);
                }
                sb.Append(block);
                updated = sb.ToString();
                result = ApplyResult.Injected;
            }

            if (updated == original)
            {
                return ApplyResult.NoOp;
            }

            File.WriteAllText(filePath, updated);
            DaroLog.Info("Editor", $"[AI KB] {result} → {filePath}");
            return result;
        }

        // Removes the marker block from `filePath`. Returns true if a block
        // was present and removed; false if no block or file missing.
        internal static bool Clean(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            var original = File.ReadAllText(filePath);
            var match = BlockRegex.Match(original);
            if (!match.Success)
            {
                return false;
            }

            // Remove the block. If we also captured a preceding blank-line
            // separator that we added in Apply, collapse it too. Heuristic:
            // if the chars immediately before the block are `\n\n` or `\r\n\r\n`,
            // strip one of them so we don't leave a stray blank.
            var before = original.Substring(0, match.Index);
            var after = original.Substring(match.Index + match.Length);
            before = TrimTrailingBlankLineSeparator(before);

            var updated = before + after;
            File.WriteAllText(filePath, updated);
            DaroLog.Info("Editor", $"[AI KB] Clean → {filePath}");
            return true;
        }

        internal static bool HasMarker(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;
            return BlockRegex.IsMatch(File.ReadAllText(filePath));
        }

        private static string BuildBlock(string payload, string newline)
        {
            // payload may be single-line or multi-line. Normalize trailing
            // newline so the block always reads:
            //
            //   <begin><newline>
            //   <payload (without trailing newline)><newline>
            //   <end><newline>
            //
            // The block ends with a newline so consecutive appends stay clean.
            var trimmed = payload ?? string.Empty;
            // Strip a single trailing newline if present (we always emit one).
            if (trimmed.EndsWith("\r\n")) trimmed = trimmed.Substring(0, trimmed.Length - 2);
            else if (trimmed.EndsWith("\n")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            // Normalize line endings inside payload to match the file's line ending.
            trimmed = NormalizeLineEndings(trimmed, newline);

            var sb = new StringBuilder();
            sb.Append(MarkerBegin).Append(newline);
            sb.Append(trimmed).Append(newline);
            sb.Append(MarkerEnd).Append(newline);
            return sb.ToString();
        }

        private static string DetectLineEnding(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\n";
            var lf = text.IndexOf('\n');
            if (lf < 0) return "\n";
            return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        }

        private static bool EndsWithNewline(string text)
        {
            return text.EndsWith("\n") || text.EndsWith("\r\n");
        }

        private static string NormalizeLineEndings(string text, string newline)
        {
            // Cheap normalize — split on \n, strip \r, rejoin with target newline.
            if (string.IsNullOrEmpty(text)) return text;
            var parts = text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].EndsWith("\r"))
                    parts[i] = parts[i].Substring(0, parts[i].Length - 1);
            }
            return string.Join(newline, parts);
        }

        // Drops one trailing blank-line separator (matches the one Apply adds
        // when appending). Idempotent — only removes one separator, never
        // shrinks the original content past that.
        private static string TrimTrailingBlankLineSeparator(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.EndsWith("\r\n\r\n"))
                return text.Substring(0, text.Length - 2);
            if (text.EndsWith("\n\n"))
                return text.Substring(0, text.Length - 1);
            return text;
        }
    }
}
