#nullable enable
using System.Globalization;

namespace Daro.Internal
{
    /// <summary>
    /// Minimal JSON value extractor for the iOS bridge's flat event payload
    /// (sketch §"Event JSON Schema"). The payload is a single-level object
    /// emitted by <c>DaroUnityBridge.mm</c> — no nesting, no arrays, keys are
    /// fixed and known at compile time. Handcrafted because pulling in a
    /// general JSON library for this single shape would be over-spec.
    /// </summary>
    /// <remarks>
    /// <para>Tolerance contract — any helper returns the safe default rather
    /// than throwing when the payload is malformed (missing key, unclosed
    /// string, garbage bytes). Callers cannot assume native shim never
    /// emits a corrupt payload, so dispatch must never crash on parse.</para>
    /// </remarks>
    internal static class DaroJsonHelpers
    {
        /// <summary>
        /// Extract a string value for the given key. Returns <c>null</c> if
        /// the key is missing, the value is a JSON <c>null</c> literal, or
        /// the value is not a quoted string. Escape sequences <c>\"</c>,
        /// <c>\\</c>, <c>\n</c>, <c>\t</c>, <c>\r</c> are decoded.
        /// </summary>
        internal static string? GetJsonString(string json, string key)
        {
            int i = SeekValue(json, key);
            if (i < 0 || i >= json.Length) return null;
            if (json[i] != '"') return null;
            i++;

            var sb = new System.Text.StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    sb.Append(next switch
                    {
                        '"'  => '"',
                        '\\' => '\\',
                        'n'  => '\n',
                        't'  => '\t',
                        'r'  => '\r',
                        '/'  => '/',
                        _    => next,
                    });
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return null; // unclosed string
        }

        /// <summary>
        /// Extract an integer value for the given key. Returns
        /// <paramref name="defaultValue"/> if the key is missing, the value
        /// is <c>null</c>, or the value is not parseable as <see cref="int"/>.
        /// </summary>
        internal static int GetJsonInt(string json, string key, int defaultValue = 0)
        {
            var raw = ReadRawNumber(json, key);
            if (raw == null) return defaultValue;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : defaultValue;
        }

        /// <summary>
        /// Extract a nullable double value. Returns <c>null</c> if the key is
        /// missing, the value is the JSON <c>null</c> literal, or the value
        /// is not parseable as <see cref="double"/>.
        /// </summary>
        internal static double? GetJsonDouble(string json, string key)
        {
            var raw = ReadRawNumber(json, key);
            if (raw == null) return null;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : (double?)null;
        }

        // ── helpers ──────────────────────────────────────────────────────

        // Returns index of the first non-whitespace char of the value for `key`,
        // or -1 if key is missing.
        private static int SeekValue(string json, string key)
        {
            string marker = "\"" + key + "\":";
            int idx = json.IndexOf(marker, System.StringComparison.Ordinal);
            if (idx < 0) return -1;
            int i = idx + marker.Length;
            while (i < json.Length && IsWhitespace(json[i])) i++;
            return i;
        }

        // Returns the raw number/null token (whitespace stripped, no quotes)
        // for `key`, or null if key is missing OR value is the JSON null literal
        // OR value is a quoted string (caller asked for a number).
        private static string? ReadRawNumber(string json, string key)
        {
            int start = SeekValue(json, key);
            if (start < 0 || start >= json.Length) return null;

            // null literal
            if (start + 4 <= json.Length
                && json[start] == 'n' && json[start + 1] == 'u'
                && json[start + 2] == 'l' && json[start + 3] == 'l')
                return null;

            // not a number — quoted string or unexpected
            if (json[start] == '"') return null;

            int end = start;
            while (end < json.Length)
            {
                char c = json[end];
                if (c == ',' || c == '}' || c == ']' || IsWhitespace(c)) break;
                end++;
            }
            if (end == start) return null;
            return json.Substring(start, end - start);
        }

        private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';
    }
}
