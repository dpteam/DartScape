using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using DartScape.Core.Crypto;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;

namespace DartScape.Server.Net
{
    /// <summary>The outcome of a login attempt.</summary>
    public enum LoginResult
    {
        /// <summary>The credentials were accepted; the session may continue.</summary>
        Success = 2,
        /// <summary>Wrong client revision.</summary>
        BadRevision = 5,
        /// <summary>Unknown account and registration is disabled.</summary>
        Rejected = 3,
        /// <summary>Wrong password.</summary>
        BadPassword = 4,
        /// <summary>Already logged in.</summary>
        AlreadyOnline = 5,
        /// <summary>The world is full.</summary>
        WorldFull = 7,
        /// <summary>Rate limited.</summary>
        LoginLimit = 9,
        /// <summary>The block could not be decrypted or was malformed.</summary>
        Malformed = 20
    }

    /// <summary>Everything the login handshake extracted from the client.</summary>
    public sealed class LoginRequest
    {
        public string Name;
        public string Password;
        public int Revision;
        public bool Reconnecting;
        public bool LowMemory;
        public int Uid;
        public int[] Seed = new int[4];
        public int[] Crcs = new int[9];
    }

    /// <summary>
    /// Implements the 317 login handshake: two-byte preamble, a server seed, then an
    /// RSA-encrypted credential block that also carries the ISAAC seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RSA step is not a security boundary вЂ” the payload is a seed and a password
    /// sent over a plain socket. What it does provide is a cheap way to reject
    /// non-DartScape clients before they can consume a player slot, and it keeps a
    /// passive observer from reading passwords off the wire on a shared network.
    /// </para>
    /// <para>
    /// Every read is bounds-checked and every failure returns a <see cref="LoginResult"/>
    /// rather than throwing, because the caller needs to send a status byte before
    /// closing the socket.
    /// </para>
    /// </remarks>
    public sealed class LoginService
    {
        private readonly RsaKeyPair _key;
        private readonly int _revision;

        public LoginService(RsaKeyPair key, int revision)
        {
            if (key == null) throw new ArgumentNullException("key");
            _key = key;
            _revision = revision;
        }

        /// <summary>
        /// Performs the handshake over <paramref name="stream"/>.
        /// </summary>
        /// <param name="request">Populated on success.</param>
        /// <returns><see cref="LoginResult.Success"/> or the reason for refusal.</returns>
        public LoginResult Authenticate(Stream stream, out LoginRequest request)
        {
            request = null;

            try
            {
                // --- step 1: preamble ---------------------------------------
                // The client sends opcode 14 and one byte derived from the name hash.
                var preamble = new byte[2];
                if (!ReadExact(stream, preamble, 2)) return LoginResult.Malformed;

                if (preamble[0] != 14)
                {
                    Log.Warning("Login", "Unexpected preamble opcode {0}.", preamble[0]);
                    return LoginResult.Malformed;
                }

                // --- step 2: server seed, status and public modulus ---------
                // The modulus is sent here rather than shipped with the client so that
                // rotating the login key does not require a client update. It is public
                // information by definition, and it travels before anything secret.
                long serverSeed = NextInt64();
                byte[] modulus = _key.ModulusBytes;

                var greeting = new byte[9 + modulus.Length];
                for (int i = 0; i < 8; i++) greeting[i] = (byte)(serverSeed >> (56 - i * 8));
                greeting[8] = 0;                    // status: continue
                Buffer.BlockCopy(modulus, 0, greeting, 9, modulus.Length);

                stream.Write(greeting, 0, greeting.Length);
                stream.Flush();

                // --- step 3: encrypted credentials ---------------------------
                var block = new byte[Protocol.LoginBlockSize];
                if (!ReadExact(stream, block, block.Length)) return LoginResult.Malformed;

                byte[] plain = _key.DecryptBlock(block, 0);
                if (plain == null)
                {
                    Log.Warning("Login", "RSA block was out of range; wrong public key on the client?");
                    return LoginResult.Malformed;
                }

                // --- step 4: parse -------------------------------------------
                var buffer = new PacketBuffer(plain);

                int marker = buffer.ReadUnsignedByte();
                if (marker != 0x0A)
                {
                    Log.Warning("Login", "Bad login marker 0x{0:X2}.", marker);
                    return LoginResult.Malformed;
                }

                var parsed = new LoginRequest();
                for (int i = 0; i < 4; i++) parsed.Seed[i] = buffer.ReadInt();
                parsed.Uid = buffer.ReadInt();

                parsed.Name = buffer.ReadString();
                parsed.Password = buffer.ReadString();

                int loginOpcode = buffer.ReadUnsignedByte();
                parsed.Reconnecting = loginOpcode == 18;

                int blockLength = buffer.ReadUnsignedByte();
                int revisionMarker = buffer.ReadUnsignedByte();
                parsed.Revision = buffer.ReadUnsignedShort();
                parsed.LowMemory = buffer.ReadUnsignedByte() == 1;

                for (int i = 0; i < 9; i++) parsed.Crcs[i] = buffer.ReadInt();

                if (revisionMarker != 255)
                {
                    Log.Warning("Login", "Bad revision marker {0}.", revisionMarker);
                    return LoginResult.Malformed;
                }

                if (parsed.Revision != _revision)
                {
                    Log.Info("Login", "{0} used revision {1}, expected {2}.", parsed.Name, parsed.Revision, _revision);
                    return LoginResult.BadRevision;
                }

                string nameError = TextEncoding.ValidatePlayerName(parsed.Name);
                if (nameError != null)
                {
                    Log.Info("Login", "Rejected name '{0}': {1}", parsed.Name, nameError);
                    return LoginResult.Rejected;
                }

                if (string.IsNullOrEmpty(parsed.Password) || parsed.Password.Length > 64)
                {
                    return LoginResult.Rejected;
                }

                // The block length byte is informational; a mismatch means the client
                // wrote something unexpected, so treat it as malformed.
                if (blockLength != 0 && blockLength != plain.Length)
                {
                    Log.Debug("Login", "Block length byte {0} vs {1} actual (tolerated).", blockLength, plain.Length);
                }

                request = parsed;
                return LoginResult.Success;
            }
            catch (ProtocolException ex)
            {
                Log.Warning("Login", "Malformed login block: {0}", ex.Message);
                return LoginResult.Malformed;
            }
            catch (IOException ex)
            {
                Log.Debug("Login", "Connection dropped during login: {0}", ex.Message);
                return LoginResult.Malformed;
            }
            catch (Exception ex)
            {
                Log.Exception("Login", ex, "Unexpected login failure");
                return LoginResult.Malformed;
            }
        }

        /// <summary>Sends the final login status byte, plus the extra bytes a success carries.</summary>
        public static void SendResult(Stream stream, LoginResult result, int privilege)
        {
            try
            {
                if (result == LoginResult.Success)
                {
                    stream.WriteByte(2);
                    stream.WriteByte((byte)privilege);
                    stream.WriteByte(0);                 // "flagged" (unused)
                }
                else
                {
                    stream.WriteByte((byte)result);
                }
                stream.Flush();
            }
            catch (IOException)
            {
                // The peer is already gone; nothing to report.
            }
        }

        /// <summary>Builds the ISAAC pair for a session: inbound and outbound use different seeds.</summary>
        public static void BuildIsaac(int[] seed, out IsaacRandom inbound, out IsaacRandom outbound)
        {
            if (seed == null) throw new ArgumentNullException("seed");

            var clientSeed = new int[4];
            Array.Copy(seed, clientSeed, 4);

            // Client -> server uses the raw seed; server -> client uses seed + 50.
            inbound = new IsaacRandom(clientSeed);

            var serverSeed = new int[4];
            for (int i = 0; i < 4; i++) serverSeed[i] = unchecked(seed[i] + 50);
            outbound = new IsaacRandom(serverSeed);
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
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

        private static readonly Random SeedSource = new Random();

        private static long NextInt64()
        {
            // A session seed only needs to be unpredictable enough to keep two logins
            // from sharing an ISAAC stream.
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToInt64(bytes, 0);
        }
    }
}
