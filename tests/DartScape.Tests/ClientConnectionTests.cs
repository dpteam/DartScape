using System;
using System.Threading;
using DartScape.Client;
using DartScape.Client.Net;

namespace DartScape.Tests
{
    /// <summary>
    /// End-to-end test of the client's own networking code against a live server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live probe in <see cref="LiveServerTests"/> reimplements the handshake, so it
    /// proves the server is correct but says nothing about the client. This one drives the
    /// real <see cref="GameClient"/>: the same login, the same ISAAC setup, the same packet
    /// reader thread.
    /// </para>
    /// <para>
    /// That distinction matters. A bug that set <c>NetworkStream.ReadTimeout</c> to zero
    /// killed the connection the instant login succeeded, and the handshake-only probe
    /// could never have seen it: the server logged a successful login and the client was
    /// already dead.
    /// </para>
    /// </remarks>
    public static class ClientConnectionTests
    {
        public static void Run(string address)
        {
            Harness.Section("Client connection");

            string host;
            int port;

            if (!SplitAddress(address, out host, out port))
            {
                Console.WriteLine("  SKIP  could not parse the server address: " + address);
                return;
            }

            Harness.Test("the real client logs in and reaches the connected state", () =>
            {
                var config = new ClientConfig { Host = host, Port = port };
                var world = new ClientWorld();
                var client = new GameClient(config, world);

                try
                {
                    client.Connect(host, port, "Probe" + Rng(), "probe-password");

                    // The whole login happens on a background thread; wait for it to land.
                    ConnectionState state = WaitForState(client, TimeSpan.FromSeconds(20));

                    Harness.Equal((int)ConnectionState.Connected, (int)state,
                        "connection state (failure reason: " + (client.LastError ?? "none") + ")");

                    // Give the reader thread a moment to apply the initial packets.
                    Harness.True(WaitFor(() => world.MyIndex >= 0, TimeSpan.FromSeconds(5)),
                        "the server told us our player index");

                    Harness.True(WaitFor(() => world.BaseLevel[0] > 0, TimeSpan.FromSeconds(5)),
                        "the skill block arrived");

                    Harness.True(WaitFor(() => world.Chat.Count > 0, TimeSpan.FromSeconds(5)),
                        "the welcome message arrived");

                    Harness.True(world.MyX > 0 && world.MyY > 0,
                        "the position arrived (" + world.MyX + "," + world.MyY + ")");

                    // The local player must be a real object in the player list: movement,
                    // rendering and every interaction read it through ClientWorld.Me.
                    Harness.NotNull(world.Me, "the local player object");
                    Harness.True(world.Me.IsLocal, "the local player is marked local");
                    Harness.True(world.Me.Name.Length > 0, "the local player has a name");
                }
                finally
                {
                    client.Disconnect();
                }
            });

            Harness.Test("the client survives an idle connection without dropping it", () =>
            {
                // This is the specific regression: a zero read timeout throws the moment
                // the first blocking read is set up, and a too-short one kills an idle
                // session. Two seconds of silence is enough to catch both.
                var config = new ClientConfig { Host = host, Port = port };
                var world = new ClientWorld();
                var client = new GameClient(config, world);

                try
                {
                    client.Connect(host, port, "Probe" + Rng(), "probe-password");

                    ConnectionState state = WaitForState(client, TimeSpan.FromSeconds(20));
                    Harness.Equal((int)ConnectionState.Connected, (int)state, "connected");

                    Thread.Sleep(2000);

                    Harness.Equal((int)ConnectionState.Connected, (int)client.State,
                        "still connected after two seconds of silence");
                }
                finally
                {
                    client.Disconnect();
                }
            });
        }

        private static ConnectionState WaitForState(GameClient client, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (client.State == ConnectionState.Connected) return client.State;
                if (client.State == ConnectionState.Failed) return client.State;
                if (client.State == ConnectionState.Disconnected && client.LastError != null) return client.State;

                Thread.Sleep(50);
            }

            return client.State;
        }

        private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }

            return condition();
        }

        private static string Rng()
        {
            // A fresh account each run so the probe never collides with real data.
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
