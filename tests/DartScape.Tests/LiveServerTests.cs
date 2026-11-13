using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using DartScape.Core.Crypto;
using DartScape.Core.Protocol;

namespace DartScape.Tests
{
    /// <summary>
    /// A live-server probe: performs the real login handshake and reads the initial
    /// packets, exactly as the client does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that proves the two halves actually talk to each other. The unit
    /// tests cover each side in isolation; this one would fail if the RSA block layout,
    /// the ISAAC seed offset, the framing or the initial packet set disagreed.
    /// </para>
    /// <para>
    /// It runs only when a server address is supplied, because it needs a running server.
    /// CI starts one before invoking the runner.
    /// </para>
    /// </remarks>
    public static class LiveServerTests
    {
        private const int LoginBlockSize = 128;

        public static void Run(string address)
        {
            Harness.Section("Live server");

            string host;
            int port;

            if (!SplitAddress(address, out host, out port))
            {
                Console.WriteLine("  SKIP  could not parse the server address: " + address);
                return;
            }

            Harness.Test("the server accepts a login and sends its initial state", () =>
            {
                string name = "Probe" + Rng();

                using (var socket = new TcpClient())
                {
                    IAsyncResult connect = socket.BeginConnect(host, port, null, null);
                    Harness.True(connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(8)),
                        "connection to " + host + ":" + port);
                    socket.EndConnect(connect);

                    socket.NoDelay = true;
                    socket.ReceiveTimeout = 8000;

                    using (NetworkStream stream = socket.GetStream())
                    {
                        // --- preamble ----------------------------------------
                        var preamble = new byte[2];
                        preamble[0] = 14;
                        preamble[1] = (byte)(NameHash(name) >> 16 & 31);
                        stream.Write(preamble, 0, 2);
                        stream.Flush();

                        // --- greeting: seed + status + modulus ----------------
                        var header = new byte[9];
                        ReadExact(stream, header, header.Length);

                        Harness.Equal(0, header[8], "server status byte (0 = continue)");

                        var modulusBytes = new byte[LoginBlockSize];
                        ReadExact(stream, modulusBytes, modulusBytes.Length);

                        long serverSeed = 0;
                        for (int i = 0; i < 8; i++) serverSeed = (serverSeed << 8) | header[i];

                        // --- credential block ---------------------------------
                        var seed = new int[4];
                        var random = new Random();
                        seed[0] = random.Next(1, 99999999);
                        seed[1] = random.Next(1, 99999999);
                        seed[2] = (int)(serverSeed >> 32);
                        seed[3] = (int)serverSeed;

                        var block = new PacketBuffer(256);
                        block.WriteByte(0x0A);
                        for (int i = 0; i < 4; i++) block.WriteInt(seed[i]);
                        block.WriteInt(random.Next(1, int.MaxValue));
                        block.WriteString(name);
                        block.WriteString("probe-password");
                        block.WriteByte(16);
                        block.WriteByte(0);
                        block.WriteByte(255);
                        block.WriteShort(317);
                        block.WriteByte(0);
                        for (int i = 0; i < 9; i++) block.WriteInt(0);

                        byte[] plain = block.ToArray();
                        var padded = new byte[LoginBlockSize];
                        Buffer.BlockCopy(plain, 0, padded, 0, Math.Min(plain.Length, padded.Length));

                        var modulus = RsaKeyPair.FromBigEndian(modulusBytes, 0, modulusBytes.Length);
                        var key = new RsaKeyPair(modulus, new System.Numerics.BigInteger(65537),
                            System.Numerics.BigInteger.Zero);

                        byte[] cipher = key.EncryptBlock(padded, 0);
                        stream.Write(cipher, 0, cipher.Length);
                        stream.Flush();

                        // --- ISAAC --------------------------------------------
                        var outbound = new IsaacRandom(new[] { seed[0], seed[1], seed[2], seed[3] });
                        var inboundSeed = new int[4];
                        for (int i = 0; i < 4; i++) inboundSeed[i] = unchecked(seed[i] + 50);
                        var inbound = new IsaacRandom(inboundSeed);

                        // --- login status -------------------------------------
                        int status = stream.ReadByte();
                        Harness.Equal(2, status, "login status (2 = success)");

                        Harness.True(stream.ReadByte() >= 0, "privilege byte");
                        Harness.True(stream.ReadByte() >= 0, "flagged byte");

                        // --- read the initial packets -------------------------
                        var seen = new System.Collections.Generic.HashSet<int>();
                        var frame = new byte[3];
                        var payload = new byte[8192];

                        socket.ReceiveTimeout = 4000;

                        try
                        {
                            for (int i = 0; i < 40; i++)
                            {
                                if (!ReadExactOrFail(stream, frame, 3)) break;

                                int opcode = (frame[0] ^ inbound.NextInt()) & 0xFF;
                                int length = (frame[1] << 8) | frame[2];

                                if (length < 0 || length > payload.Length) break;
                                if (length > 0 && !ReadExactOrFail(stream, payload, length)) break;

                                seen.Add(opcode);

                                // 85 = absolute position, 134 = skills, 90 = container,
                                // 253 = a chat message. The welcome messages come after the
                                // state, so all four have to be seen before stopping.
                                if (seen.Contains(85) && seen.Contains(134) &&
                                    seen.Contains(90) && seen.Contains(253))
                                {
                                    break;
                                }
                            }
                        }
                        catch (IOException)
                        {
                            // Timeout after the initial burst: expected, and fine.
                        }

                        Harness.True(seen.Contains(85), "the server sent the player position (opcode 85)");
                        Harness.True(seen.Contains(134), "the server sent the skill block (opcode 134)");
                        Harness.True(seen.Contains(90), "the server sent the inventory (opcode 90)");
                        Harness.True(seen.Contains(253), "the server sent a chat message (opcode 253)");
                    }
                }
            });
        }

        private static bool ReadExactOrFail(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;

            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }

            return true;
        }

        private static void ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            Harness.True(ReadExactOrFail(stream, buffer, count),
                "server closed the connection while sending " + count + " bytes");
        }

        private static long NameHash(string name)
        {
            long value = 0;
            foreach (char c in (name ?? string.Empty).ToLowerInvariant()) value = value * 37 + (c - 96);
            return value;
        }

        private static string Rng()
        {
            // A fresh account each run, so the probe never collides with real data.
            return (Math.Abs(Environment.TickCount) % 100000).ToString();
        }

        private static bool SplitAddress(string text, out string host, out int port)
        {
            host = null;
            port = 43594;

            if (string.IsNullOrEmpty(text)) return false;

            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                host = text.Substring(0, colon);

                int parsed;
                if (int.TryParse(text.Substring(colon + 1), out parsed)) port = parsed;
                return host.Length > 0;
            }

            host = text;
            return true;
        }
    }
}
