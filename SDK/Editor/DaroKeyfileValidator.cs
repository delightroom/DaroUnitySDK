using System;
using System.Security.Cryptography;
using System.Text;

namespace Daro.Editor
{
    // Editor-side keyfile validator — verifies that a (daroAppKey,
    // keyfileText) pair decrypts cleanly *before* the Android/iOS build runs.
    //
    // **Why this exists**: the Daro gradle plugin only attempts decryption
    // during `compileReleaseKotlin` configuration phase. A bad pair surfaces
    // as a multi-minute build → "Tag mismatch" buried in gradle stdout +
    // mojibake Korean message. This validator gives O(ms) feedback in the
    // IM window so consumers catch typos / wrong-issuance pairs before
    // committing to a real build.
    //
    // **Crypto scheme**:
    //
    //   key       = SHA-256(daroAppKey.getBytes("UTF-8"))         // 32 bytes (AES-256)
    //   raw       = Base64.getDecoder().decode(keyfileText)
    //   nonce     = raw[0..12]                                     // standard 12-byte GCM nonce
    //   cipher    = raw[12..raw.length - 16]
    //   tag       = raw[raw.length - 16..raw.length]               // 128-bit auth tag
    //   plaintext = AES/GCM/NoPadding(key, nonce, cipher, tag)     // throws AEADBadTagException on mismatch
    //
    // The decrypted plaintext is JSON (DaroConfig) but for *validation*
    // purposes we only need the auth tag check.
    //
    // **Crypto implementation** uses our own DaroAesGcm helper (atop
    // AES-ECB) instead of `System.Security.Cryptography.AesGcm` — Unity 6's
    // Mono runtime on macOS throws PlatformNotSupportedException for
    // AesGcm, even though the type compiles. DaroAesGcm works on every
    // Mono platform via Aes-ECB primitives. See DaroAesGcm.cs.
    //
    // **Cross-platform (iOS) assumption**: the iOS Daro plugin is assumed
    // to use the same AES-GCM + SHA-256 scheme. Verified against Android
    // side; iOS side confirms at first iOS build (forward verification).
    internal static class DaroKeyfileValidator
    {
        internal const int NonceSize = DaroAesGcm.NonceSize;
        internal const int TagSize   = DaroAesGcm.TagSize;

        internal enum Result
        {
            Valid,
            EmptyAppKey,
            NoKeyfile,
            InvalidBase64,
            TooShort,
            TagMismatch,
        }

        // Pure decision function — no Unity APIs, no side effects.
        internal static Result Validate(string daroAppKey, string keyfileText)
        {
            if (string.IsNullOrEmpty(daroAppKey)) return Result.EmptyAppKey;
            if (string.IsNullOrEmpty(keyfileText)) return Result.NoKeyfile;

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(keyfileText.Trim());
            }
            catch (FormatException)
            {
                return Result.InvalidBase64;
            }

            if (raw.Length < NonceSize + TagSize)
                return Result.TooShort;

            byte[] keyBytes;
            using (var sha = SHA256.Create())
            {
                keyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(daroAppKey));
            }

            var nonce = new byte[NonceSize];
            Buffer.BlockCopy(raw, 0, nonce, 0, NonceSize);

            var cipherLen = raw.Length - NonceSize - TagSize;
            var cipher = new byte[cipherLen];
            Buffer.BlockCopy(raw, NonceSize, cipher, 0, cipherLen);

            var tag = new byte[TagSize];
            Buffer.BlockCopy(raw, raw.Length - TagSize, tag, 0, TagSize);

            return DaroAesGcm.Verify(keyBytes, nonce, cipher, tag)
                ? Result.Valid
                : Result.TagMismatch;
        }
    }
}
