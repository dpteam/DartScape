using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DartScape.Core.Diagnostics;

namespace DartScape.Core.Crypto
{
    /// <summary>
    /// RSA used for the 317 login handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 317 protocol does not sign or verify anything вЂ” RSA is only used to hide the
    /// ISAAC seed from a passive observer, and (more importantly for us) to prove that
    /// the peer speaks our handshake. The client encrypts a 128-byte block with the
    /// server's public key; the server decrypts it and checks the marker bytes.
    /// </para>
    /// <para>
    /// Implemented on top of <see cref="BigInteger"/> (part of the .NET Framework, so
    /// no third-party crypto library is needed). Raw RSA without padding is
    /// intentional: it matches the protocol and the payload is a seed, not a secret
    /// message. Keys are stored as plain hex in <c>keys\server.key</c>.
    /// </para>
    /// </remarks>
    public sealed class RsaKeyPair
    {
        /// <summary>Size of the login block in bytes. 317 uses a 1024-bit key.</summary>
        public const int BlockSize = 128;

        private readonly BigInteger _modulus;
        private readonly BigInteger _publicExponent;
        private readonly BigInteger _privateExponent;

        public RsaKeyPair(BigInteger modulus, BigInteger publicExponent, BigInteger privateExponent)
        {
            _modulus = modulus;
            _publicExponent = publicExponent;
            _privateExponent = privateExponent;
        }

        /// <summary>Modulus as a big-endian byte array (what the client needs).</summary>
        public byte[] ModulusBytes
        {
            get { return ToBigEndian(_modulus, BlockSize); }
        }

        /// <summary>Public exponent (65537 for all real 317 servers).</summary>
        public BigInteger PublicExponent
        {
            get { return _publicExponent; }
        }

        /// <summary>True when the private half is present, i.e. this can decrypt.</summary>
        public bool CanDecrypt
        {
            get { return !_privateExponent.IsZero; }
        }

        /// <summary>
        /// Decrypts a 128-byte block. Returns <c>null</c> when the ciphertext is out of
        /// range, which is exactly what happens when a peer uses the wrong key вЂ” the
        /// login code turns that into a "bad session id" disconnect.
        /// </summary>
        public byte[] DecryptBlock(byte[] cipher, int offset)
        {
            if (!CanDecrypt) throw new InvalidOperationException("Key has no private exponent.");
            if (cipher == null) throw new ArgumentNullException("cipher");
            if (offset < 0 || offset + BlockSize > cipher.Length) throw new ArgumentOutOfRangeException("offset");

            BigInteger c = FromBigEndian(cipher, offset, BlockSize);
            if (c >= _modulus) return null;

            BigInteger m = BigInteger.ModPow(c, _privateExponent, _modulus);
            return ToBigEndian(m, BlockSize);
        }

        /// <summary>Encrypts a 128-byte block. Only used by tests and the key tool.</summary>
        public byte[] EncryptBlock(byte[] plain, int offset)
        {
            if (plain == null) throw new ArgumentNullException("plain");
            if (offset < 0 || offset + BlockSize > plain.Length) throw new ArgumentOutOfRangeException("offset");

            BigInteger m = FromBigEndian(plain, offset, BlockSize);
            BigInteger c = BigInteger.ModPow(m, _publicExponent, _modulus);
            return ToBigEndian(c, BlockSize);
        }

        // -- persistence ------------------------------------------------------

        /// <summary>
        /// Loads a key file, generating and saving a fresh 1024-bit key when the file
        /// is missing or unreadable. A missing key must never stop the server from
        /// starting вЂ” it just invalidates existing client sessions, which is fine.
        /// </summary>
        public static RsaKeyPair LoadOrCreate(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");

            try
            {
                if (File.Exists(path))
                {
                    RsaKeyPair existing = Load(path);
                    if (existing != null) return existing;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Crypto", "Could not load key {0}: {1}. Generating a new one.", path, ex.Message);
            }

            RsaKeyPair generated = Generate(1024);
            try
            {
                generated.Save(path);
                Log.Info("Crypto", "Generated new RSA key at {0}.", path);
            }
            catch (Exception ex)
            {
                Log.Warning("Crypto", "Could not save key {0}: {1}. Using it in memory only.", path, ex.Message);
            }
            return generated;
        }

        /// <summary>Loads a key from the text format written by <see cref="Save"/>.</summary>
        public static RsaKeyPair Load(string path)
        {
            string[] lines = File.ReadAllLines(path);
            BigInteger n = BigInteger.Zero, e = BigInteger.Zero, d = BigInteger.Zero;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "modulus": n = FromHex(value); break;
                    case "publicexponent": e = FromHex(value); break;
                    case "privateexponent": d = FromHex(value); break;
                }
            }

            if (n.IsZero || e.IsZero)
            {
                Log.Warning("Crypto", "Key file {0} is missing modulus/exponent.", path);
                return null;
            }

            return new RsaKeyPair(n, e, d);
        }

        /// <summary>Writes the key in a human-inspectable hex format.</summary>
        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder(1024);
            sb.AppendLine("# DartScape RSA key вЂ” 317 login handshake");
            sb.AppendLine("# Keep server.key private. server.public.key is safe to ship in the client.");
            sb.AppendLine("modulus=" + ToHex(_modulus));
            sb.AppendLine("publicexponent=" + ToHex(_publicExponent));
            if (CanDecrypt) sb.AppendLine("privateexponent=" + ToHex(_privateExponent));
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        /// <summary>
        /// Generates a key with the given bit length using the platform's CSPRNG.
        /// Only the primes come from <see cref="RSACryptoServiceProvider"/>; the
        /// arithmetic stays ours so the protocol's raw (unpadded) form works.
        /// </summary>
        public static RsaKeyPair Generate(int bits)
        {
            if (bits < 512 || bits > 4096 || (bits % 8) != 0)
            {
                throw new ArgumentOutOfRangeException("bits", "Key size must be a multiple of 8 between 512 and 4096.");
            }

            // RSACryptoServiceProvider gives us well-tested prime generation, and its
            // ExportParameters gives us n, e, d directly.
            using (var rsa = new RSACryptoServiceProvider(bits))
            {
                try
                {
                    RSAParameters p = rsa.ExportParameters(true);
                    BigInteger n = FromBigEndian(p.Modulus, 0, p.Modulus.Length);
                    BigInteger e = FromBigEndian(p.Exponent, 0, p.Exponent.Length);
                    BigInteger d = FromBigEndian(p.D, 0, p.D.Length);
                    return new RsaKeyPair(n, e, d);
                }
                catch (CryptographicException ex)
                {
                    // Extremely old CryptoAPI providers refuse ExportParameters(true).
                    Log.Warning("Crypto", "CSP refused key export ({0}); falling back to managed generation.", ex.Message);
                }
            }

            return GenerateManaged(bits);
        }

        /// <summary>
        /// Managed fallback generator (probabilistic Miller-Rabin). Only used when the
        /// platform CSP cannot export a private key, which happens on some stripped
        /// Windows XP images.
        /// </summary>
        private static RsaKeyPair GenerateManaged(int bits)
        {
            Log.Warning("Crypto", "Using managed RSA key generation ({0} bits). This may take a moment.", bits);

            int pBits = bits / 2;
            int qBits = bits - pBits;
            BigInteger e = new BigInteger(65537);

            BigInteger p, q, n, phi, d;

            int guard = 0;
            while (true)
            {
                if (++guard > 64) throw new CryptographicException("Could not generate a usable RSA key pair.");

                p = RandomPrime(pBits);
                q = RandomPrime(qBits);
                if (p == q) continue;

                n = p * q;
                if (BigIntUtil.GetBitLength(n) != bits) continue;

                phi = (p - 1) * (q - 1);
                if (BigInteger.GreatestCommonDivisor(e, phi) != BigInteger.One) continue;

                d = ModInverse(e, phi);
                if (d.IsZero) continue;
                break;
            }

            return new RsaKeyPair(n, e, d);
        }

        private static BigInteger RandomPrime(int bits)
        {
            var rng = RandomNumberGenerator.Create();
            try
            {
                var bytes = new byte[bits / 8 + 1];
                while (true)
                {
                    rng.GetBytes(bytes);
                    bytes[bytes.Length - 1] = 0;              // keep the top byte positive
                    bytes[0] |= 0xC0;                          // force the top two bits
                    bytes[bytes.Length - 1] |= 0x01;           // force odd

                    BigInteger candidate = FromBigEndian(bytes, 0, bytes.Length);
                    if (BigIntUtil.GetBitLength(candidate) != bits) continue;
                    if (IsProbablePrime(candidate, 24)) return candidate;
                }
            }
            finally
            {
                rng.Dispose();
            }
        }

        private static bool IsProbablePrime(BigInteger n, int rounds)
        {
            if (n < 2) return false;
            if (n == 2 || n == 3) return true;
            if (n.IsEven) return false;

            // n - 1 = d * 2^s
            BigInteger d = n - 1;
            int s = 0;
            while (d.IsEven) { d = d >> 1; s++; }

            var rng = RandomNumberGenerator.Create();
            try
            {
                var buf = new byte[BigIntUtil.GetByteCount(n) + 1];
                for (int i = 0; i < rounds; i++)
                {
                    rng.GetBytes(buf);
                    buf[buf.Length - 1] = 0;
                    BigInteger a = FromBigEndian(buf, 0, buf.Length) % (n - 3) + 2;

                    BigInteger x = BigInteger.ModPow(a, d, n);
                    if (x == 1 || x == n - 1) continue;

                    bool witnessed = true;
                    for (int r = 1; r < s; r++)
                    {
                        x = BigInteger.ModPow(x, 2, n);
                        if (x == n - 1) { witnessed = false; break; }
                    }
                    if (witnessed) return false;
                }
                return true;
            }
            finally
            {
                rng.Dispose();
            }
        }

        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            BigInteger g = m, x = 0, x1 = 1, a1 = a % m;
            if (a1.IsZero) return BigInteger.Zero;

            while (a1 > 1)
            {
                BigInteger q = g / a1;
                BigInteger t = x1;
                x1 = x - q * x1;
                x = t;
                t = a1;
                a1 = g - q * a1;
                g = t;
            }
            if (x.Sign < 0) x += m;
            return x;
        }

        // -- byte helpers -----------------------------------------------------

        /// <summary>Reads a big-endian unsigned integer from a fixed-width slice.</summary>
        public static BigInteger FromBigEndian(byte[] buffer, int offset, int length)
        {
            var little = new byte[length + 1];
            for (int i = 0; i < length; i++)
            {
                little[i] = buffer[offset + length - 1 - i];
            }
            return new BigInteger(little);
        }

        /// <summary>Writes an unsigned integer big-endian, left-padded to <paramref name="length"/>.</summary>
        public static byte[] ToBigEndian(BigInteger value, int length)
        {
            byte[] raw = value.ToByteArray();          // little-endian, maybe with a sign byte
            var result = new byte[length];

            for (int i = 0; i < length && i < raw.Length; i++)
            {
                result[length - 1 - i] = raw[i];
            }
            return result;
        }

        /// <summary>Parses a hex string (either endianness) into a positive integer.</summary>
        public static BigInteger FromHex(string hex)
        {
            hex = hex.Trim();
            if (hex.Length == 0) return BigInteger.Zero;

            var big = new byte[hex.Length / 2];
            for (int i = 0; i < big.Length; i++)
            {
                big[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return FromBigEndian(big, 0, big.Length);
        }

        /// <summary>Formats an integer as a big-endian hex string.</summary>
        public static string ToHex(BigInteger value)
        {
            byte[] big = ToBigEndian(value, BigIntUtil.GetByteCount(value));
            var sb = new StringBuilder(big.Length * 2);
            foreach (byte b in big) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
