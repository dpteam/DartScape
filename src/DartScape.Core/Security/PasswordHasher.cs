using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DartScape.Core.Security
{
    /// <summary>
    /// Password hashing with PBKDF2-HMAC-SHA256.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET Framework 4.0 ships <see cref="Rfc2898DeriveBytes"/>, but only in its SHA-1
    /// form, and SHA-1 based PBKDF2 is not something to put in front of real accounts in
    /// 2026. <see cref="HMACSHA256"/> is available, so the PBKDF2 construction is
    /// implemented directly — it is a short, well-specified loop and there is nothing to
    /// get creative about.
    /// </para>
    /// <para>
    /// Stored format is self-describing so the parameters can be raised later without
    /// invalidating existing accounts:
    /// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-hex&gt;$&lt;hash-hex&gt;</c>.
    /// </para>
    /// </remarks>
    public static class PasswordHasher
    {
        /// <summary>Iteration count for new hashes.</summary>
        public const int DefaultIterations = 12000;

        /// <summary>Salt length in bytes.</summary>
        public const int SaltBytes = 16;

        /// <summary>Derived key length in bytes.</summary>
        public const int HashBytes = 32;

        /// <summary>Produces a storable hash string for <paramref name="password"/>.</summary>
        public static string Hash(string password, int iterations = DefaultIterations)
        {
            if (password == null) throw new ArgumentNullException("password");

            var salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            byte[] derived = Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashBytes);

            return string.Format(CultureInfo.InvariantCulture,
                "pbkdf2-sha256${0}${1}${2}", iterations, ToHex(salt), ToHex(derived));
        }

        /// <summary>
        /// Verifies a password against a stored hash. Always performs the full derivation,
        /// even when the stored string is malformed, so timing does not leak whether the
        /// account exists.
        /// </summary>
        public static bool Verify(string password, string stored)
        {
            if (password == null || stored == null) return false;

            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256")
            {
                // Still burn the time, then fail.
                Pbkdf2(Encoding.UTF8.GetBytes(password), new byte[SaltBytes], DefaultIterations, HashBytes);
                return false;
            }

            int iterations;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out iterations) ||
                iterations < 1 || iterations > 1000000)
            {
                return false;
            }

            byte[] salt = FromHex(parts[2]);
            byte[] expected = FromHex(parts[3]);
            if (salt == null || expected == null) return false;

            byte[] actual = Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, expected.Length);
            return FixedEquals(actual, expected);
        }

        /// <summary>
        /// PBKDF2 with HMAC-SHA256 (RFC 8018). Standard construction: one block, since the
        /// derived key never exceeds the HMAC output size here.
        /// </summary>
        public static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int length)
        {
            if (password == null) throw new ArgumentNullException("password");
            if (salt == null) throw new ArgumentNullException("salt");
            if (iterations < 1) throw new ArgumentOutOfRangeException("iterations");
            if (length < 1) throw new ArgumentOutOfRangeException("length");

            const int hashLength = 32;
            int blocks = (length + hashLength - 1) / hashLength;
            var output = new byte[blocks * hashLength];

            using (var hmac = new HMACSHA256(password))
            {
                for (int block = 1; block <= blocks; block++)
                {
                    var input = new byte[salt.Length + 4];
                    Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
                    input[salt.Length] = (byte)(block >> 24);
                    input[salt.Length + 1] = (byte)(block >> 16);
                    input[salt.Length + 2] = (byte)(block >> 8);
                    input[salt.Length + 3] = (byte)block;

                    byte[] u = hmac.ComputeHash(input);
                    var accumulator = new byte[u.Length];
                    Buffer.BlockCopy(u, 0, accumulator, 0, u.Length);

                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int k = 0; k < accumulator.Length; k++) accumulator[k] ^= u[k];
                    }

                    Buffer.BlockCopy(accumulator, 0, output, (block - 1) * hashLength, hashLength);
                }
            }

            if (output.Length == length) return output;

            var trimmed = new byte[length];
            Buffer.BlockCopy(output, 0, trimmed, 0, length);
            return trimmed;
        }

        /// <summary>Constant-time comparison, so verification cannot be timed.</summary>
        public static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        /// <summary>Constant-time comparison for strings of any length.</summary>
        public static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;

            int difference = a.Length ^ b.Length;
            int length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return null;

            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                byte value;
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                {
                    return null;
                }
                result[i] = value;
            }
            return result;
        }
    }
}
