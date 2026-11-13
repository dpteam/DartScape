using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using DartScape.Core.Crypto;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;
using DartScape.Server.Data;
using DartScape.Server.Definitions;
using DartScape.Server.Game;

namespace DartScape.Server.Net
{
    /// <summary>
    /// One client connection: owns the socket, runs the login, then decodes packets and
    /// hands the resulting actions to the world tick thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Framing is uniform and self-describing: every packet in either direction is
    /// <c>[opcode][length:2][payload]</c>, with the opcode XORed against the session's
    /// ISAAC stream. The stock 317 protocol uses a fixed size table instead; that table
    /// is exactly the kind of thing that silently desynchronises a connection when one
    /// side adds a packet, so this build uses an explicit length and documents the
    /// deviation.
    /// </para>
    /// <para>
    /// Reading is blocking on a dedicated thread. That is the simplest thing that works
    /// on .NET 4.0 and Windows XP, and at a few hundred players it is entirely adequate.
    /// </para>
    /// </remarks>
    public sealed class GameSession : IPlayerEndpoint
    {
        /// <summary>Hard cap on a single inbound payload.</summary>
        private const int MaxPayload = 8192;

        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly World _world;
        private readonly ServerConfig _config;
        private readonly LoginService _login;
        private readonly PlayerStore _store;

        private readonly object _writeLock = new object();

        private IsaacRandom _inboundIsaac;
        private IsaacRandom _outboundIsaac;
        private Player _player;
        private volatile bool _closed;
        private string _remoteAddress = "?";

        public GameSession(Socket socket, World world, ServerConfig config, LoginService login, PlayerStore store)
        {
            _socket = socket;
            _world = world;
            _config = config;
            _login = login;
            _store = store;

            _socket.NoDelay = true;
            _stream = new NetworkStream(socket, false);

            try
            {
                _remoteAddress = socket.RemoteEndPoint == null ? "?" : socket.RemoteEndPoint.ToString();
            }
            catch (Exception)
            {
                _remoteAddress = "?";
            }
        }

        /// <summary>The player once logged in, or <c>null</c>.</summary>
        public Player Player
        {
            get { return _player; }
        }

        public string RemoteAddress
        {
            get { return _remoteAddress; }
        }

        public bool IsClosed
        {
            get { return _closed; }
        }

        /// <summary>Runs the connection until it closes. Intended as a thread body.</summary>
        public void Run()
        {
            try
            {
                _socket.ReceiveTimeout = _config.LoginTimeoutMs;

                LoginRequest request;
                LoginResult result = _login.Authenticate(_stream, out request);

                if (result != LoginResult.Success)
                {
                    LoginService.SendResult(_stream, result, 0);
                    Log.Info("Login", "Rejected {0}: {1}", _remoteAddress, result);
                    return;
                }

                LoginService.BuildIsaac(request.Seed, out _inboundIsaac, out _outboundIsaac);

                // Load or create the account.
                Player player = _store.LoadOrCreate(request.Name, request.Password, out bool created);
                if (player == null)
                {
                    LoginService.SendResult(_stream, LoginResult.Rejected, 0);
                    Log.Info("Login", "Refused {0}: account '{1}' could not be loaded.", _remoteAddress, request.Name);
                    return;
                }

                if (player.Bank == null) { /* Bank is always allocated in the constructor. */ }

                player.Endpoint = this;
                player.Privilege = _store.GetPrivilege(player.NameKey);

                string error = _world.AddPlayer(player);
                if (error != null)
                {
                    LoginService.SendResult(_stream, LoginResult.WorldFull, 0);
                    Log.Info("Login", "Refused {0}: {1}", _remoteAddress, error);
                    return;
                }

                _player = player;
                _socket.ReceiveTimeout = _config.IdleTimeoutMs;

                LoginService.SendResult(_stream, LoginResult.Success, player.Privilege);
                Log.Info("Login", "{0} logged in from {1}{2}.", player.Name, _remoteAddress, created ? " (new account)" : "");

                SendInitialState(player);

                ReadLoop();
            }
            catch (SocketException ex)
            {
                Log.Debug("Session", "{0} socket closed: {1}", _remoteAddress, ex.SocketErrorCode);
            }
            catch (IOException ex)
            {
                Log.Debug("Session", "{0} stream closed: {1}", _remoteAddress, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Exception("Session", ex, "Session " + _remoteAddress + " failed");
            }
            finally
            {
                Close("connection ended");
            }
        }

        private void SendInitialState(Player player)
        {
            _world.SendSkills(player);
            _world.SendInventory(player);

            // The client must know where it is before it can render anything.
            Send(Protocol.OutPlayerPosition, buffer =>
            {
                buffer.WriteByte(player.Position.Plane);
                buffer.WriteShortLE(player.Position.X);
                buffer.WriteShortLE(player.Position.Y);
            });

            // Give the player something to look at: the local NPCs are picked up by the
            // normal update loop once the client reports the region as loaded.
            player.RegionLoaded = true;
            player.View.Clear();

            _world.SendMessage(player, "Welcome to " + _config.Name + ", " + player.Name + ".");
            _world.SendMessage(player, "Type ::help for a list of commands.");
        }

        // -- packet loop ------------------------------------------------------

        private void ReadLoop()
        {
            var header = new byte[3];
            var payload = new byte[MaxPayload];

            while (!_closed)
            {
                if (!ReadExact(header, 3)) break;

                int opcode = (header[0] ^ _inboundIsaac.NextInt()) & 0xFF;
                int length = (header[1] << 8) | header[2];

                if (length < 0 || length > MaxPayload)
                {
                    Log.Warning("Session", "{0} sent an oversized packet ({1} bytes, opcode {2}).", _player.Name, length, opcode);
                    break;
                }

                if (length > 0 && !ReadExact(payload, length)) break;

                var buffer = new PacketBuffer(payload, 0, length);
                Dispatch(opcode, buffer);
            }
        }

        private void Dispatch(int opcode, PacketBuffer buffer)
        {
            Player player = _player;
            if (player == null) return;

            // Handlers run on the tick thread so they can touch world state safely.
            _world.Enqueue(() => HandlePacket(player, opcode, buffer));
        }

        private void HandlePacket(Player player, int opcode, PacketBuffer buffer)
        {
            if (player == null || !player.Online) return;
            player.LastActivityUtc = DateTime.UtcNow;

            try
            {
                PacketHandlers.Handle(_world, player, opcode, buffer);
            }
            catch (ProtocolException ex)
            {
                Log.Warning("Session", "{0} sent a malformed {1} packet: {2}", player.Name, Protocol.NameOf(opcode), ex.Message);
                Disconnect("Malformed packet.");
            }
            catch (Exception ex)
            {
                Log.Exception("Session", ex, "Handler for " + Protocol.NameOf(opcode) + " failed for " + player.Name);
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

        /// <summary>Queues one packet. Safe to call from any thread.</summary>
        public void Send(int opcode, Action<PacketBuffer> writer)
        {
            if (_closed) return;

            var payload = new PacketBuffer(256);
            if (writer != null) writer(payload);

            byte[] body = payload.ToArray();
            if (body.Length > MaxPayload)
            {
                Log.Warning("Session", "Dropping oversized outbound packet {0} ({1} bytes).", opcode, body.Length);
                return;
            }

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
                catch (ObjectDisposedException)
                {
                    _closed = true;
                }
            }
        }

        /// <summary>Sends a chat-box message.</summary>
        public void SendMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            Send(Protocol.OutMessage, buffer =>
            {
                buffer.WriteString(text);
                buffer.WriteByte(0);       // message type: game message
            });
        }

        /// <summary>Closes the connection and removes the player from the world.</summary>
        public void Disconnect(string reason)
        {
            Close(reason);
        }

        private void Close(string reason)
        {
            if (_closed) return;
            _closed = true;

            Player player = _player;
            if (player != null && player.Online)
            {
                try
                {
                    _store.Save(player);
                }
                catch (Exception ex)
                {
                    Log.Exception("Session", ex, "Saving " + player.Name + " on disconnect");
                }

                _world.RemovePlayer(player, reason);
            }

            try { _stream.Dispose(); } catch (Exception) { }
            try { _socket.Close(); } catch (Exception) { }
        }
    }
}
