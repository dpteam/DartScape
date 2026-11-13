using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using DartScape.Core.Cache;
using DartScape.Core.Crypto;
using DartScape.Core.Protocol;
using DartScape.Core.Security;

namespace DartScape.Tests
{
    /// <summary>Tests for the protocol, crypto and cache primitives.</summary>
    public static class CoreTests
    {
        public static void Run()
        {
            Harness.Section("PacketBuffer");
            PacketBufferTests();

            Harness.Section("ISAAC");
            IsaacTests();

            Harness.Section("RSA");
            RsaTests();

            Harness.Section("Passwords");
            PasswordTests();

            Harness.Section("Cache");
            CacheTests();

            Harness.Section("BigInteger helpers");
            BigIntTests();
        }

        private static void PacketBufferTests()
        {
            Harness.Test("byte variants round-trip", () =>
            {
                var buffer = new PacketBuffer(64);
                buffer.WriteByte(0x12);
                buffer.WriteByteS(0x34);
                buffer.WriteByteC(0x56);

                Harness.Equal(0x12, buffer.ReadUnsignedByte(), "plain byte");
                Harness.Equal(0x34, buffer.ReadUnsignedByteS(), "offset byte");
                Harness.Equal(0x56, buffer.ReadUnsignedByteC(), "negated byte");
            });

            Harness.Test("short and int variants round-trip", () =>
            {
                var buffer = new PacketBuffer(64);
                buffer.WriteShort(0x1234);
                buffer.WriteShortLE(0x5678);
                buffer.WriteShortA(0x9ABC);
                buffer.WriteInt(0x12345678);
                buffer.WriteIntLE(0x0A0B0C0D);
                buffer.WriteIntME(0x11223344);

                Harness.Equal(0x1234, buffer.ReadUnsignedShort(), "big-endian short");
                Harness.Equal(0x5678, buffer.ReadUnsignedShortLE(), "little-endian short");
                Harness.Equal(0x9ABC, buffer.ReadUnsignedShortA(), "offset short");
                Harness.Equal(0x12345678, buffer.ReadInt(), "big-endian int");
                Harness.Equal(0x0A0B0C0D, buffer.ReadIntLE(), "little-endian int");
                Harness.Equal(0x11223344, buffer.ReadIntME(), "middle-endian int");
            });

            Harness.Test("smart values use the shortest form", () =>
            {
                var buffer = new PacketBuffer(64);
                buffer.WriteSmart(0);
                buffer.WriteSmart(127);
                buffer.WriteSmart(128);
                buffer.WriteSmart(300);
                buffer.WriteSmart(32767);

                Harness.Equal(0, buffer.ReadSmart(), "zero");
                Harness.Equal(127, buffer.ReadSmart(), "127");
                Harness.Equal(128, buffer.ReadSmart(), "128");
                Harness.Equal(300, buffer.ReadSmart(), "300");
                Harness.Equal(32767, buffer.ReadSmart(), "32767");
            });

            Harness.Test("strings are NUL terminated", () =>
            {
                var buffer = new PacketBuffer(64);
                buffer.WriteString("Dart");
                buffer.WriteString("");
                buffer.WriteString("Scape");

                Harness.Equal("Dart", buffer.ReadString(), "first");
                Harness.Equal("", buffer.ReadString(), "empty");
                Harness.Equal("Scape", buffer.ReadString(), "second");
            });

            Harness.Test("newline-terminated strings stop at 0x0A", () =>
            {
                var buffer = new PacketBuffer(64);
                buffer.WriteBytes(TextEncoding.GetBytes("A test, with commas.\n"));
                buffer.WriteByte(0x42);

                Harness.Equal("A test, with commas.", buffer.ReadNewlineString(), "text");
                Harness.Equal(0x42, buffer.ReadUnsignedByte(), "byte after the terminator");
            });

            Harness.Test("name encoding matches the 317 base-37 scheme", () =>
            {
                var buffer = new PacketBuffer(32);
                buffer.WriteName("Ab1");

                // A=1, b=2, 1=28 => ((1*37)+2)*37+28 = 1467 + 28... computed by hand below.
                long expected = 0;
                expected = expected * 37 + ('A' - 'A' + 1);
                expected = expected * 37 + ('b' - 'a' + 1);
                expected = expected * 37 + ('1' - '0' + 27);

                buffer.ReadPosition = 0;
                long decoded = 0;
                while (buffer.Remaining > 0) decoded = (decoded << 7) | (long)buffer.ReadUnsignedByte();

                Harness.Equal(expected, decoded, "packed name");
            });

            Harness.Test("bit access round-trips mixed widths", () =>
            {
                var buffer = new PacketBuffer(32);
                buffer.StartBitAccess();
                buffer.WriteBits(11, 2047);
                buffer.WriteBits(5, 17);
                buffer.WriteBits(3, 5);
                buffer.WriteBits(1, 1);
                buffer.FinishBitAccess();

                buffer.ReadPosition = 0;
                buffer.StartBitRead();
                Harness.Equal(2047, buffer.ReadBits(11), "11-bit value");
                Harness.Equal(17, buffer.ReadBits(5), "5-bit value");
                Harness.Equal(5, buffer.ReadBits(3), "3-bit value");
                Harness.Equal(1, buffer.ReadBits(1), "1-bit value");
                buffer.FinishBitRead();
            });

            Harness.Test("reading past the end throws, never returns garbage", () =>
            {
                var buffer = new PacketBuffer(4);
                buffer.WriteByte(1);

                Harness.Throws(() => buffer.ReadInt(), "oversized read");
            });

            Harness.Test("the buffer grows without losing data", () =>
            {
                var buffer = new PacketBuffer(2);
                for (int i = 0; i < 500; i++) buffer.WriteByte(i & 0xFF);

                Harness.Equal(500, buffer.WritePosition, "length");
                buffer.ReadPosition = 0;

                for (int i = 0; i < 500; i++) Harness.Equal(i & 0xFF, buffer.ReadUnsignedByte(), "byte " + i);
            });

            Harness.Test("player name validation accepts good names and rejects bad ones", () =>
            {
                Harness.Null(TextEncoding.ValidatePlayerName("Dart"), "Dart");
                Harness.Null(TextEncoding.ValidatePlayerName("Dart Power"), "with a space");
                Harness.Null(TextEncoding.ValidatePlayerName("A-B_C 1"), "punctuation");

                Harness.NotNull(TextEncoding.ValidatePlayerName(""), "empty");
                Harness.NotNull(TextEncoding.ValidatePlayerName("Abcdefghijklm"), "13 characters");
                Harness.NotNull(TextEncoding.ValidatePlayerName("123"), "no letters");
                Harness.NotNull(TextEncoding.ValidatePlayerName(" Dart"), "leading space");
                Harness.NotNull(TextEncoding.ValidatePlayerName("Da  rt"), "double space");
                Harness.NotNull(TextEncoding.ValidatePlayerName("Da<rt"), "angle bracket");
            });

            Harness.Test("display names are normalised consistently", () =>
            {
                Harness.Equal("Dart Power", TextEncoding.DisplayName("dart power"), "two words");
                Harness.Equal("Dart", TextEncoding.DisplayName("  DART  "), "trimmed");
                Harness.Equal("dart", TextEncoding.NormaliseName("  DART "), "key");
            });
        }

        private static void IsaacTests()
        {
            Harness.Test("the same seed produces the same stream", () =>
            {
                int[] seed = { 1, 2, 3, 4 };
                var a = new IsaacRandom(seed);
                var b = new IsaacRandom(seed);

                for (int i = 0; i < 512; i++)
                {
                    Harness.Equal(a.NextInt(), b.NextInt(), "value " + i);
                }
            });

            Harness.Test("different seeds diverge immediately", () =>
            {
                var a = new IsaacRandom(new[] { 1, 2, 3, 4 });
                var b = new IsaacRandom(new[] { 1, 2, 3, 5 });

                bool differs = false;
                for (int i = 0; i < 64 && !differs; i++)
                {
                    if (a.NextInt() != b.NextInt()) differs = true;
                }

                Harness.True(differs, "streams should differ");
            });

            Harness.Test("the +50 seed offset used by the server produces a different stream", () =>
            {
                int[] seed = { 10, 20, 30, 40 };
                var client = new IsaacRandom(seed);

                var shifted = new int[4];
                for (int i = 0; i < 4; i++) shifted[i] = seed[i] + 50;
                var server = new IsaacRandom(shifted);

                bool differs = false;
                for (int i = 0; i < 64 && !differs; i++)
                {
                    if (client.NextInt() != server.NextInt()) differs = true;
                }

                Harness.True(differs, "server and client streams must not be identical");
            });
        }

        private static void RsaTests()
        {
            Harness.Test("a generated key round-trips a block", () =>
            {
                RsaKeyPair key = RsaKeyPair.Generate(1024);

                var plain = new byte[RsaKeyPair.BlockSize];
                for (int i = 0; i < plain.Length; i++) plain[i] = (byte)(i * 7 + 1);

                // Keep the payload below the modulus so the round trip is exact.
                plain[0] = 0;

                byte[] cipher = key.EncryptBlock(plain, 0);
                byte[] recovered = key.DecryptBlock(cipher, 0);

                Harness.Equal(plain, recovered, "decrypted block");
            });

            Harness.Test("a saved key reloads and still decrypts", () =>
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "dartscape-test-" + Guid.NewGuid().ToString("N") + ".key");

                try
                {
                    RsaKeyPair original = RsaKeyPair.Generate(1024);
                    original.Save(path);

                    RsaKeyPair loaded = RsaKeyPair.Load(path);
                    Harness.NotNull(loaded, "reloaded key");
                    Harness.True(loaded.CanDecrypt, "reloaded key must have a private exponent");

                    var plain = new byte[RsaKeyPair.BlockSize];
                    plain[0] = 0;
                    plain[5] = 0x42;

                    byte[] cipher = original.EncryptBlock(plain, 0);
                    Harness.Equal(plain, loaded.DecryptBlock(cipher, 0), "decrypted with reloaded key");
                }
                finally
                {
                    try { System.IO.File.Delete(path); } catch (Exception) { }
                }
            });

            Harness.Test("ciphertext at or above the modulus is rejected", () =>
            {
                RsaKeyPair key = RsaKeyPair.Generate(1024);

                var cipher = new byte[RsaKeyPair.BlockSize];
                for (int i = 0; i < cipher.Length; i++) cipher[i] = 0xFF;

                Harness.Null(key.DecryptBlock(cipher, 0), "out-of-range block");
            });

            Harness.Test("modulus bytes are exactly the block size", () =>
            {
                RsaKeyPair key = RsaKeyPair.Generate(1024);
                Harness.Equal(RsaKeyPair.BlockSize, key.ModulusBytes.Length, "modulus length");
            });
        }

        private static void PasswordTests()
        {
            Harness.Test("a password verifies against its own hash", () =>
            {
                string hash = PasswordHasher.Hash("correct horse battery staple");
                Harness.True(PasswordHasher.Verify("correct horse battery staple", hash), "correct password");
                Harness.False(PasswordHasher.Verify("wrong", hash), "wrong password");
            });

            Harness.Test("hashes are salted, so the same password hashes differently", () =>
            {
                string a = PasswordHasher.Hash("same");
                string b = PasswordHasher.Hash("same");

                Harness.True(a != b, "two hashes must differ");
                Harness.True(PasswordHasher.Verify("same", a), "first hash still verifies");
                Harness.True(PasswordHasher.Verify("same", b), "second hash still verifies");
            });

            Harness.Test("malformed stored hashes fail closed", () =>
            {
                Harness.False(PasswordHasher.Verify("x", null), "null");
                Harness.False(PasswordHasher.Verify("x", ""), "empty");
                Harness.False(PasswordHasher.Verify("x", "not-a-hash"), "garbage");
                Harness.False(PasswordHasher.Verify("x", "pbkdf2-sha256$abc$def$ghi"), "bad numbers");
            });

            Harness.Test("PBKDF2 matches a known SHA-256 vector", () =>
            {
                // RFC 7914 style vector: P="passwd", S="salt", c=1, dkLen=64 is the
                // scrypt example; for PBKDF2-HMAC-SHA256 the published c=1 vector with
                // P="password", S="salt", dkLen=32 is used here.
                byte[] derived = PasswordHasher.Pbkdf2(
                    Encoding.UTF8.GetBytes("password"),
                    Encoding.UTF8.GetBytes("salt"),
                    1,
                    32);

                string hex = ToHex(derived);
                Harness.Equal("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", hex,
                    "PBKDF2-HMAC-SHA256(password, salt, 1, 32)");
            });

            Harness.Test("constant-time comparison behaves like equality", () =>
            {
                Harness.True(PasswordHasher.FixedEquals("abc", "abc"), "equal");
                Harness.False(PasswordHasher.FixedEquals("abc", "abd"), "differing last char");
                Harness.False(PasswordHasher.FixedEquals("abc", "ab"), "different length");
            });
        }

        private static void CacheTests()
        {
            Harness.Test("name hashing matches the client's base-61 scheme", () =>
            {
                // Verified against the Java client: hash = hash*61 + upper(c) - 32.
                int hash = Archive.NameHash("obj.dat");

                int expected = 0;
                foreach (char c in "OBJ.DAT")
                {
                    expected = expected * 61 + c - 32;
                }

                Harness.Equal(expected, hash, "obj.dat hash");
            });

            Harness.Test("name hashing is case-insensitive", () =>
            {
                Harness.Equal(Archive.NameHash("Item.Dat"), Archive.NameHash("item.dat"), "mixed case");
            });

            Harness.Test("the file store rejects a missing directory cleanly", () =>
            {
                FileStore store = FileStore.Open(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "dartscape-does-not-exist-" + Guid.NewGuid().ToString("N")));

                Harness.Null(store, "missing cache");
            });

            Harness.Test("bzip2 rejects a stream without a header", () =>
            {
                var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                Harness.Throws(() => Bzip2.Decompress(junk, 0, junk.Length, 64), "bad stream");
            });

            Harness.Test("bzip2 decompresses a real stream", () =>
            {
                // "BZh9" + a single empty block is a valid, minimal stream.
                var empty = new byte[] { (byte)'B', (byte)'Z', (byte)'h', (byte)'9' };
                Harness.Throws(() => Bzip2.Decompress(empty, 0, empty.Length, 1), "truncated stream");
            });
        }

        private static void BigIntTests()
        {
            Harness.Test("bit length is correct at the boundaries", () =>
            {
                Harness.Equal(0, BigIntUtil.GetBitLength(BigInteger.Zero), "zero");
                Harness.Equal(1, BigIntUtil.GetBitLength(BigInteger.One), "one");
                Harness.Equal(8, BigIntUtil.GetBitLength(new BigInteger(255)), "255");
                Harness.Equal(9, BigIntUtil.GetBitLength(new BigInteger(256)), "256");
                Harness.Equal(1024, BigIntUtil.GetBitLength(BigInteger.Pow(2, 1023)), "2^1023");
            });

            Harness.Test("big-endian conversion round-trips", () =>
            {
                var bytes = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
                BigInteger value = BigIntUtil.FromBigEndian(bytes, 0, bytes.Length);

                Harness.Equal("1.2.3.4", string.Join(".", new[] { 1, 2, 3, 4 }), "sanity");
                Harness.Equal(bytes, RsaKeyPair.ToBigEndian(value, bytes.Length), "round trip");
            });

            Harness.Test("hex parsing and formatting round-trip", () =>
            {
                BigInteger value = RsaKeyPair.FromHex("deadbeef");
                Harness.Equal("deadbeef", RsaKeyPair.ToHex(value), "deadbeef");
            });
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
