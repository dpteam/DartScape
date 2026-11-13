using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using DartScape.Core.Cache;
using DartScape.Core.Crypto;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;


namespace DartScape.Client.Net
{
    /// <summary>Connection states the client UI reacts to.</summary>
    public enum ConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        LoggingIn = 2,
        Connected = 3,
        Failed = 4
    }

    /// <summary>
    /// The client's connection: login handshake, then a packet loop that mutates the
    /// local world model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The socket is read on a background thread and every decoded packet is applied
    /// immediately to <see cref="World"/>. The renderer reads that model from the UI
    /// thread; because the model is a plain object graph and the reader only ever appends
    /// or replaces whole entries, a torn read costs at most one frame of a stale position,
    /// never a crash.
    /// </para>
    /// <para>
    /// Outbound packets go through a lock. They are small and infrequent enough that a
    /// queue would be more machinery than it is worth.
    /// </para>
    /// </remarks>
    public sealed class GameClient
    {
        /// <summary>Size of the RSA login block: 128 bytes for a 1024-bit key.</summary>
        public const int LoginBlockSize = 128;

        private readonly ClientConfig _config;
        private readonly ClientWorld _world;

        private TcpClient _socket;
        private NetworkStream _stream;
        private Thread _reader;
        private IsaacRandom _inboundIsaac;
        private IsaacRandom _outboundIsaac;
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        public GameClient(ClientConfig config, ClientWorld world)
        {
            _config = config;
            _world = world;
        }

        /// <summary>Current connection state.</summary>
        public ConnectionState State { get; private set; }

        /// <summary>Human readable reason for the last failure.</summary>
        public string LastError { get; private set; }

        /// <summary>Raised when the state changes, on the reader thread.</summary>
        public event Action<ConnectionState> StateChanged;

        /// <summary>Round-trip time to the server in milliseconds, or -1.</summary>
        public int PingMs = -1;

        private void SetState(ConnectionState state, string error = null)
        {
            State = state;
            if (error != null) LastError = error;

            Action<ConnectionState> handler = StateChanged;
            if (handler != null)
            {
                try { handler(state); } catch (Exception) { }
            }
        }

        /// <summary>Connects and logs in on a background thread.</summary>
        public void Connect(string host, int port, string name, string password)
        {
            if (State == ConnectionState.Connecting || State == ConnectionState.LoggingIn) return;

            SetState(ConnectionState.Connecting);

            var thread = new Thread(() => ConnectWorker(host, port, name, password))
            {
                IsBackground = true,
                Name = "DartScape.Login"
            };
            thread.Start();
        }

        private void ConnectWorker(string host, int port, string name, string password)
        {
            try
            {
                _socket = new TcpClient();
                _socket.NoDelay = true;

                IAsyncResult result = _socket.BeginConnect(host, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(8)))
                {
                    SetState(ConnectionState.Failed, "Connection timed out.");
                    return;
                }
                _socket.EndConnect(result);

                _stream = _socket.GetStream();
                _stream.ReadTimeout = 15000;
                _stream.WriteTimeout = 15000;

                SetState(ConnectionState.LoggingIn);

                if (!Handshake(name, password))
                {
                    return;
                }

                // Blocking reads from here on. NetworkStream rejects 0: its setter only
                // accepts Timeout.Infinite (-1) or a positive value, and 0 throws
                // ArgumentOutOfRangeException, which is exactly the failure this used to
                // produce the instant login succeeded.
                _stream.ReadTimeout = Timeout.Infinite;
                SetState(ConnectionState.Connected);

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DartScape.Reader" };
                _reader.Start();
            }
            catch (SocketException ex)
            {
                SetState(ConnectionState.Failed, "Cannot reach " + host + ":" + port + " (" + ex.SocketErrorCode + ").");
            }
            catch (IOException ex)
            {
                SetState(ConnectionState.Failed, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Exception("Client", ex, "Login failed");
                SetState(ConnectionState.Failed, ex.Message);
            }
        }

        // -- handshake --------------------------------------------------------

        private bool Handshake(string name, string password)
        {
            // Step 1: preamble.
            var preamble = new byte[2];
            preamble[0] = 14;
            preamble[1] = (byte)(NameHash(name) >> 16 & 31);
            _stream.Write(preamble, 0, 2);
            _stream.Flush();

            // Step 2: seed + status + public modulus.
            var header = new byte[9];
            if (!ReadExact(header, 9)) { SetState(ConnectionState.Failed, "Server closed the connection."); return false; }

            if (header[8] != 0)
            {
                SetState(ConnectionState.Failed, "Server is not accepting logins (" + header[8] + ").");
                return false;
            }

            var modulusBytes = new byte[128];
            if (!ReadExact(modulusBytes, modulusBytes.Length))
            {
                SetState(ConnectionState.Failed, "Server sent a truncated key.");
                return false;
            }

            // Step 3: build the credential block.
            var seed = new int[4];
            long serverSeed = 0;
            for (int i = 0; i < 8; i++) serverSeed = (serverSeed << 8) | header[i];

            var random = new Random();
            seed[0] = random.Next(1, 99999999);
            seed[1] = random.Next(1, 99999999);
            seed[2] = (int)(serverSeed >> 32);
            seed[3] = (int)serverSeed;

            var block = new PacketBuffer(256);
            block.WriteByte(0x0A);
            for (int i = 0; i < 4; i++) block.WriteInt(seed[i]);
            block.WriteInt(random.Next(1, int.MaxValue));      // uid
            block.WriteString(name);
            block.WriteString(password);
            block.WriteByte(16);                               // login (18 = reconnect)
            block.WriteByte(0);                                // length byte, filled below
            block.WriteByte(255);
            block.WriteShort(_config.Revision);
            block.WriteByte(0);                                // low memory
            for (int i = 0; i < 9; i++) block.WriteInt(0);     // CRCs, unused

            byte[] plain = block.ToArray();
            var padded = new byte[LoginBlockSize];
            Buffer.BlockCopy(plain, 0, padded, 0, Math.Min(plain.Length, padded.Length));

            // Fix the informational length byte.
            padded[2 + 4 * 4 + 4 + TextEncoding.GetBytes(name).Length + 1 +
                   TextEncoding.GetBytes(password).Length + 1] = (byte)(plain.Length & 0xFF);

            // Step 4: raw RSA with the server's modulus.
            var modulus = RsaKeyPair.FromBigEndian(modulusBytes, 0, modulusBytes.Length);
            var key = new RsaKeyPair(modulus, new System.Numerics.BigInteger(65537), System.Numerics.BigInteger.Zero);
            byte[] cipher = key.EncryptBlock(padded, 0);

            _stream.Write(cipher, 0, cipher.Length);
            _stream.Flush();

            // Step 5: ISAAC streams.
            LoginServiceCompat(seed);

            // Step 6: status.
            int status = _stream.ReadByte();
            if (status < 0)
            {
                SetState(ConnectionState.Failed, "Server closed the connection during login.");
                return false;
            }

            if (status != 2)
            {
                SetState(ConnectionState.Failed, DescribeLoginFailure(status));
                return false;
            }

            int privilege = _stream.ReadByte();
            int flagged = _stream.ReadByte();

            _world.MyPrivilege = privilege < 0 ? 0 : privilege;
            _world.AddChatMessage(flagged == 1
                ? "You have unread messages in your message centre."
                : "Welcome to DartScape.");

            return true;
        }

        /// <summary>Builds the ISAAC pair exactly as the server does.</summary>
        private void LoginServiceCompat(int[] seed)
        {
            var clientSeed = new int[4];
            Array.Copy(seed, clientSeed, 4);
            _outboundIsaac = new IsaacRandom(clientSeed);      // client -> server

            var serverSeed = new int[4];
            for (int i = 0; i < 4; i++) serverSeed[i] = unchecked(seed[i] + 50);
            _inboundIsaac = new IsaacRandom(serverSeed);       // server -> client
        }

        private static string DescribeLoginFailure(int status)
        {
            switch (status)
            {
                case 3: return "That account does not exist, or registration is closed.";
                case 4: return "Incorrect password.";
                case 5: return "Your client is out of date.";
                case 6: return "That account is already logged in.";
                case 7: return "The world is full.";
                case 9: return "Too many login attempts; please wait a moment.";
                case 20: return "The server could not read your login block.";
                default: return "Login refused (code " + status + ").";
            }
        }

        private static long NameHash(string name)
        {
            long value = 0;
            foreach (char c in (name ?? string.Empty).ToLowerInvariant())
            {
                value = value * 37 + (c - 96);
            }
            return value;
        }

        // -- packet loop ------------------------------------------------------

        private void ReadLoop()
        {
            var header = new byte[3];
            var payload = new byte[8192];

            try
            {
                while (!_closed)
                {
                    if (!ReadExact(header, 3)) break;

                    int opcode = (header[0] ^ _inboundIsaac.NextInt()) & 0xFF;
                    int length = (header[1] << 8) | header[2];

                    if (length < 0 || length > payload.Length) break;
                    if (length > 0 && !ReadExact(payload, length)) break;

                    var buffer = new PacketBuffer(payload, 0, length);
                    PacketDecoder.Handle(_world, opcode, buffer);
                }
            }
            catch (IOException)
            {
                // Normal when the server closes the socket.
            }
            catch (Exception ex)
            {
                Log.Exception("Client", ex, "Read loop failed");
            }
            finally
            {
                _closed = true;
                SetState(ConnectionState.Disconnected, "Connection closed.");
            }
        }

        private bool ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        // -- sending ----------------------------------------------------------

        /// <summary>Sends one packet. Safe from any thread.</summary>
        public void Send(int opcode, Action<PacketBuffer> writer)
        {
            if (_closed || _stream == null) return;

            var payload = new PacketBuffer(128);
            if (writer != null) writer(payload);
            byte[] body = payload.ToArray();

            var frame = new byte[3 + body.Length];
            frame[0] = (byte)(opcode ^ _outboundIsaac.NextInt());
            frame[1] = (byte)(body.Length >> 8);
            frame[2] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, frame, 3, body.Length);

            lock (_writeLock)
            {
                try
                {
                    _stream.Write(frame, 0, frame.Length);
                }
                catch (IOException)
                {
                    _closed = true;
                }
            }
        }

        /// <summary>Asks the server to walk to a tile.</summary>
        public void WalkTo(int worldX, int worldY)
        {
            ClientPlayer me = _world.Me;
            if (me == null) return;

            // The server expects the path as a sequence of one-tile steps starting from
            // the player's current tile.
            var steps = new List<int>(64);
            int x = me.X;
            int y = me.Y;

            int dx = worldX - x;
            int dy = worldY - y;
            int count = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (count <= 0) return;
            if (count > 60) count = 60;

            for (int i = 0; i < count; i++)
            {
                int stepX = Math.Sign(worldX - x);
                int stepY = Math.Sign(worldY - y);
                x += stepX;
                y += stepY;
                steps.Add(stepX);
                steps.Add(stepY);
            }

            Send(164, buffer =>
            {
                buffer.WriteShortLE(me.X);
                buffer.WriteShortLE(me.Y);
                foreach (int step in steps) buffer.WriteByte(step);
            });
        }

        /// <summary>Sends a public chat message.</summary>
        public void SendChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Send(4, buffer =>
            {
                buffer.WriteByte(0);
                buffer.WriteString(text);
            });
        }

        /// <summary>Sends a console command.</summary>
        public void SendCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Send(103, buffer => buffer.WriteString(text));
        }

        /// <summary>Sends an item interaction (first option).</summary>
        public void SendItemAction(int slot, int itemId)
        {
            Send(122, buffer =>
            {
                buffer.WriteShortA(slot);
                buffer.WriteShort(itemId);
                buffer.WriteShortA(3214);
            });
        }

        /// <summary>Sends an item drop.</summary>
        public void SendDropItem(int slot, int itemId)
        {
            Send(87, buffer =>
            {
                buffer.WriteShortA(itemId);
                buffer.WriteShortA(slot);
            });
        }

        /// <summary>Sends a pickup request.</summary>
        public void SendPickup(int itemId, int x, int y)
        {
            Send(236, buffer =>
            {
                buffer.WriteShort(itemId);
                buffer.WriteShortA(y);
                buffer.WriteShortA(x);
            });
        }

        /// <summary>Sends an NPC interaction.</summary>
        public void SendNpcAction(int index, int option)
        {
            int opcode = option == 1 ? 155 : option == 2 ? 17 : option == 3 ? 21 : 18;
            Send(opcode, buffer => buffer.WriteShort(index));
        }

        /// <summary>Closes the connection.</summary>
        public void Disconnect()
        {
            _closed = true;
            try { if (_stream != null) _stream.Dispose(); } catch (Exception) { }
            try { if (_socket != null) _socket.Close(); } catch (Exception) { }
            SetState(ConnectionState.Disconnected);
        }
    }
}
