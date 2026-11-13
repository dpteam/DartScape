using System;
using DartScape.Core.Cache;
using DartScape.Game.Data;

namespace DartScape.Game
{
    /// <summary>
    /// Process-wide handles to the long-lived game services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The world, the player store and the cache are singletons by nature: there is one
    /// world, one set of characters and one cache per process. Passing them through every
    /// constructor would add noise to a dozen call sites for no benefit, and a full
    /// dependency-injection container would be a large amount of machinery to manage three
    /// objects.
    /// </para>
    /// <para>
    /// They are set once during start-up, before any thread that could read them begins, so
    /// no locking is needed on the read path.
    /// </para>
    /// </remarks>
    public static class GameServices
    {
        /// <summary>The open cache, or <c>null</c> when the server started without one.</summary>
        public static FileStore Cache;

        /// <summary>The character store.</summary>
        public static PlayerStore Store;

        /// <summary>The running world.</summary>
        public static World World;

        /// <summary>The effective configuration.</summary>
        public static ServerConfig Config;

        /// <summary>Raised when a character is saved, so the web layer can refresh counts.</summary>
        public static event Action CharactersChanged;

        /// <summary>Notifies listeners that the character set changed.</summary>
        public static void RaiseCharactersChanged()
        {
            Action handler = CharactersChanged;
            if (handler == null) return;

            try { handler(); }
            catch (Exception) { }
        }
    }
}
