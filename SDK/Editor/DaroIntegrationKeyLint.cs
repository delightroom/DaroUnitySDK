using System;

namespace Daro.Editor
{
    // Shape verdict for the INTEGRATION KEY envelope. Ok means "plausible",
    // not "valid" — see DaroIntegrationKeyLint.
    internal enum DaroIntegrationKeyShape
    {
        Ok = 0,
        Empty,
        LegacyAppKey,
        MissingPrefix,
        InvalidBase64,
        TooShort,
    }

    // Shape lint for the INTEGRATION KEY envelope — "di" + base64(nonce[12]
    // || ciphertext || tag[16]), AES-256-GCM. The Editor deliberately does
    // NOT decrypt: the shared secret lives only in the two native tools (the
    // daro CLI and the so.daro gradle plugin), and a third copy here would
    // sit outside the ios/Tools secret-parity gate and silently break on
    // rotation. Real validation happens at build time on both platforms —
    // this lint only catches paste accidents (truncation, whitespace, a
    // legacy UUID app key pasted by habit).
    internal static class DaroIntegrationKeyLint
    {
        private const string Prefix = "di";
        private const int NonceLength = 12;
        private const int TagLength = 16;

        internal static bool LooksValid(string key) => Check(key) == DaroIntegrationKeyShape.Ok;

        // Whitespace anywhere is stripped before judging — the runtime codec
        // and the gradle plugin do the same.
        internal static DaroIntegrationKeyShape Check(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return DaroIntegrationKeyShape.Empty;

            var compact = StripWhitespace(key);

            if (!compact.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return LooksLikeLegacyAppKey(compact)
                    ? DaroIntegrationKeyShape.LegacyAppKey
                    : DaroIntegrationKeyShape.MissingPrefix;
            }

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(compact.Substring(Prefix.Length));
            }
            catch (FormatException)
            {
                return DaroIntegrationKeyShape.InvalidBase64;
            }

            return raw.Length <= NonceLength + TagLength
                ? DaroIntegrationKeyShape.TooShort
                : DaroIntegrationKeyShape.Ok;
        }

        // English description for validator rows (IM window localizes
        // separately via validate.ik.* keys).
        internal static string Describe(DaroIntegrationKeyShape shape) => shape switch
        {
            DaroIntegrationKeyShape.Empty =>
                "INTEGRATION KEY is empty.",
            DaroIntegrationKeyShape.LegacyAppKey =>
                "This looks like a legacy app key (UUID) — the unified SDK needs the INTEGRATION KEY issued by the dashboard.",
            DaroIntegrationKeyShape.MissingPrefix =>
                "INTEGRATION KEY must start with \"di\".",
            DaroIntegrationKeyShape.InvalidBase64 =>
                "INTEGRATION KEY payload is not valid base64 — likely truncated or altered.",
            DaroIntegrationKeyShape.TooShort =>
                "INTEGRATION KEY payload too short — likely truncated.",
            _ => null,
        };

        // 8-4-4-4-12 hex groups — the pre-unified per-platform app key shape.
        private static bool LooksLikeLegacyAppKey(string s)
        {
            if (s.Length != 36) return false;
            return s.Split('-').Length == 5;
        }

        private static string StripWhitespace(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
