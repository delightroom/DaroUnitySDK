using System;
using System.Security.Cryptography;

namespace Daro.Editor
{
    // Pure-managed AES-GCM (NIST SP 800-38D) — implemented atop AES-ECB
    // because Unity Editor's Mono runtime on macOS does NOT implement
    // `System.Security.Cryptography.AesGcm` (throws
    // PlatformNotSupportedException at runtime, even though the type
    // compiles). We build GCM ourselves using only Aes-ECB primitives,
    // which Mono supports on every platform.
    //
    // Two operations:
    //   - Verify(key, nonce, cipher, tag) → bool         — auth-tag check only
    //   - Encrypt(key, nonce, plaintext)  → (cipher, tag) — for test setup
    //
    // Constraints (matching Daro plugin's keyfile encryption):
    //   - 12-byte nonce (the GCM standard / fast path: J0 = nonce || 00 00 00 01)
    //   - 16-byte (128-bit) auth tag
    //   - empty AAD (no associated authenticated data)
    //   - AES-256 (key must be 32 bytes — typically SHA-256 digest)
    //
    // Why we don't depend on AesGcm:
    //   - .NET Standard 2.1 / .NET 5+ have System.Security.Cryptography.AesGcm
    //   - Mono's Bleeding Edge runtime (Unity 6) doesn't implement it on macOS
    //   - User-facing symptom: PlatformNotSupportedException at first click on
    //     IM window's Validate Key Pair button. Caught empirically 2026-04-29.
    //
    // **Crypto correctness** is verified via NIST SP 800-38D test vectors in
    // DaroAesGcmTests + round-trip tests in DaroKeyfileValidatorTests.
    internal static class DaroAesGcm
    {
        internal const int NonceSize = 12;
        internal const int TagSize   = 16;
        internal const int BlockSize = 16;

        // Verifies the AES-GCM authentication tag without recovering the
        // plaintext. Returns true iff (key, nonce, cipher) produces tag.
        // Constant-time comparison on the tag.
        internal static bool Verify(byte[] key, byte[] nonce, byte[] cipher, byte[] tag)
        {
            if (nonce == null || nonce.Length != NonceSize) return false;
            if (tag == null || tag.Length != TagSize) return false;
            if (cipher == null) return false;

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor();

            // H = AES_ECB(key, 0^128) — the "hash subkey" for GHASH.
            var h = new byte[BlockSize];
            encryptor.TransformBlock(new byte[BlockSize], 0, BlockSize, h, 0);

            // J0 = nonce || 00 00 00 01 (12-byte nonce fast path).
            var j0 = new byte[BlockSize];
            Buffer.BlockCopy(nonce, 0, j0, 0, NonceSize);
            j0[15] = 1;

            // S = GHASH_H(empty AAD, cipher).
            var s = Ghash(h, cipher);

            // ExpectedTag = AES_ECB(key, J0) XOR S.
            var aesJ0 = new byte[BlockSize];
            encryptor.TransformBlock(j0, 0, BlockSize, aesJ0, 0);

            var computed = new byte[TagSize];
            for (int i = 0; i < TagSize; i++) computed[i] = (byte)(aesJ0[i] ^ s[i]);

            // Constant-time tag compare (defense against timing oracles —
            // overkill in a build-time validator but good practice).
            int diff = 0;
            for (int i = 0; i < TagSize; i++) diff |= computed[i] ^ tag[i];
            return diff == 0;
        }

        // Encrypts plaintext under (key, nonce) producing (cipher, tag).
        // Used by test setup to produce known-valid keyfile bytes without
        // depending on AesGcm.Encrypt (same Mono-on-macOS issue).
        internal static (byte[] Cipher, byte[] Tag) Encrypt(
            byte[] key, byte[] nonce, byte[] plaintext)
        {
            if (nonce == null || nonce.Length != NonceSize)
                throw new ArgumentException($"nonce must be {NonceSize} bytes", nameof(nonce));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor();

            var h = new byte[BlockSize];
            encryptor.TransformBlock(new byte[BlockSize], 0, BlockSize, h, 0);

            var j0 = new byte[BlockSize];
            Buffer.BlockCopy(nonce, 0, j0, 0, NonceSize);
            j0[15] = 1;

            // CTR-mode encrypt — counter starts at J0+1.
            var cipher = new byte[plaintext.Length];
            var counter = new byte[BlockSize];
            Buffer.BlockCopy(j0, 0, counter, 0, BlockSize);
            IncrementCounter(counter);

            var keystream = new byte[BlockSize];
            for (int offset = 0; offset < plaintext.Length; offset += BlockSize)
            {
                encryptor.TransformBlock(counter, 0, BlockSize, keystream, 0);
                int chunk = Math.Min(BlockSize, plaintext.Length - offset);
                for (int i = 0; i < chunk; i++)
                    cipher[offset + i] = (byte)(plaintext[offset + i] ^ keystream[i]);
                IncrementCounter(counter);
            }

            var s = Ghash(h, cipher);

            var aesJ0 = new byte[BlockSize];
            encryptor.TransformBlock(j0, 0, BlockSize, aesJ0, 0);
            var tag = new byte[TagSize];
            for (int i = 0; i < TagSize; i++) tag[i] = (byte)(aesJ0[i] ^ s[i]);

            return (cipher, tag);
        }

        // Increments the rightmost 32 bits of counter (GCM convention — only
        // the last 4 bytes form the actual counter; nonce occupies bytes 0..12).
        private static void IncrementCounter(byte[] counter)
        {
            for (int i = 15; i >= 12; i--)
            {
                counter[i] = (byte)(counter[i] + 1);
                if (counter[i] != 0) return;
            }
        }

        // GHASH_H over (empty AAD, cipher) per NIST SP 800-38D §6.4.
        private static byte[] Ghash(byte[] h, byte[] cipher)
        {
            var y = new byte[BlockSize];

            // Process cipher in 16-byte blocks (last block zero-padded).
            for (int offset = 0; offset < cipher.Length; offset += BlockSize)
            {
                var block = new byte[BlockSize];
                int chunk = Math.Min(BlockSize, cipher.Length - offset);
                Buffer.BlockCopy(cipher, offset, block, 0, chunk);
                // remaining bytes already 0 (zero-pad)

                for (int i = 0; i < BlockSize; i++) y[i] ^= block[i];
                y = Gf128Multiply(y, h);
            }

            // Length block: 64-bit BE AAD-bit-length (0) || 64-bit BE cipher-bit-length.
            var lenBlock = new byte[BlockSize];
            ulong cipherBits = (ulong)cipher.Length * 8UL;
            for (int i = 0; i < 8; i++)
                lenBlock[15 - i] = (byte)(cipherBits >> (i * 8));
            // bytes 0..7 stay zero (AAD-bit-length = 0).

            for (int i = 0; i < BlockSize; i++) y[i] ^= lenBlock[i];
            y = Gf128Multiply(y, h);

            return y;
        }

        // GF(2^128) multiplication per NIST SP 800-38D Algorithm 1.
        // Bit convention: byte 0 is most significant; within each byte, bit 7
        // (MSB) is the highest-degree coefficient. Reduction polynomial:
        //     f(x) = x^128 + x^7 + x^2 + x + 1
        // After right-shift, when the bit shifted out (bit at position 127,
        // i.e., LSB of byte 15) is 1, XOR the high byte with 0xE1 — the
        // representation of f(x) with the leading x^128 term removed.
        private static byte[] Gf128Multiply(byte[] x, byte[] y)
        {
            var z = new byte[BlockSize];
            var v = new byte[BlockSize];
            Buffer.BlockCopy(y, 0, v, 0, BlockSize);

            for (int i = 0; i < 128; i++)
            {
                int byteIdx = i >> 3;
                int bitOffset = 7 - (i & 7);
                bool bitX = ((x[byteIdx] >> bitOffset) & 1) != 0;
                if (bitX)
                {
                    for (int j = 0; j < BlockSize; j++) z[j] ^= v[j];
                }

                bool vLsb = (v[15] & 1) != 0;
                for (int j = 15; j > 0; j--)
                {
                    v[j] = (byte)(((v[j - 1] & 1) << 7) | (v[j] >> 1));
                }
                v[0] = (byte)(v[0] >> 1);
                if (vLsb) v[0] ^= 0xE1;
            }
            return z;
        }
    }
}
