using System;
using DartScape.Core.Diagnostics;
using System.Collections.Generic;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// Everything the game needs from a 317 cache: the world map, floor colours and object
    /// definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three live in different places and each needs its own dance to reach: the map is
    /// numbered by slot and needs the <c>map_index</c> table to be usable at all, and the
    /// configs are buried inside a named archive. This class hides all of that behind
    /// <see cref="TryGetRegion"/>, so nothing downstream has to know the cache's layout.
    /// </para>
    /// <para>
    /// Opening is best-effort. A missing or unreadable cache leaves the properties empty rather
    /// than throwing, because the game can still run on procedural terrain вЂ” it just does not
    /// look like RuneScape.
    /// </para>
    /// </remarks>
    public sealed class GameCache : IDisposable
    {
        private const int ConfigStore = 0;
        private const int MapStore = 4;

        private readonly FileStore _store;
        private readonly Dictionary<int, Landscape> _regions = new Dictionary<int, Landscape>(64);
        private readonly object _sync = new object();

        // Fast path for the last region asked for. The renderer resolves a region several
        // thousand times per frame (four corners per tile, plus the minimap), and taking a lock
        // for each of those is measurable. A stale read here is harmless: the worst case is a
        // redundant decompress, never a wrong answer.
        private volatile Landscape _lastRegion;
        private volatile int _lastRegionKey = -1;

        /// <summary>Region table, empty when the cache could not be read.</summary>
        public MapIndex Map { get; private set; }

        /// <summary>Floor and ground definitions, empty when unavailable.</summary>
        public FloorDefinitionSet Floors { get; private set; }

        /// <summary>Object definitions, empty when unavailable.</summary>
        public ObjectDefinitionSet Objects { get; private set; }


        /// <summary>True when the world map is available.</summary>
        public bool HasMap
        {
            get { return Map != null && Map.Count > 0; }
        }

        private GameCache(FileStore store)
        {
            _store = store;
            Map = new MapIndex();
            Floors = FloorDefinitionSet.Empty;
            Objects = ObjectDefinitionSet.Empty;
        }

        /// <summary>
        /// Opens a cache directory and loads its tables.
        /// </summary>
        /// <returns><c>null</c> when the directory holds no cache.</returns>
        public static GameCache Open(string directory)
        {
            FileStore store = FileStore.Open(directory);
            if (store == null) return null;

            var cache = new GameCache(store);
            cache.LoadConfigs();

            return cache;
        }

        /// <summary>
        /// Reads the config archive: <c>map_index</c>, <c>flo.dat</c> and <c>loc.dat</c>.
        /// </summary>
        /// <remarks>
        /// All three sit inside a named archive in store 0, and the archive is addressed by a
        /// hash of the file name rather than by position. The store-0 slot holding it is not
        /// fixed across cache revisions, so every archive is probed instead of assuming one.
        /// </remarks>
        private void LoadConfigs()
        {
            int count = _store.GetFileCount(ConfigStore);

            for (int slot = 0; slot < count; slot++)
            {
                byte[] payload = _store.Read(ConfigStore, slot);
                if (payload == null || payload.Length < 16) continue;

                Archive archive;

                try
                {
                    archive = Archive.Parse(payload);
                }
                catch (Exception)
                {
                    continue;
                }

                if (archive.Count == 0) continue;

                byte[] mapIndex = GetFromArchive(archive, "map_index");
                if (mapIndex != null) Map = MapIndex.Parse(mapIndex);

                byte[] floors = GetFromArchive(archive, "flo.dat");
                if (floors != null) Floors = FloorDefinitionSet.Parse(floors);

                byte[] locIndex = GetFromArchive(archive, "loc.idx");
                byte[] locData = GetFromArchive(archive, "loc.dat");

                if (locIndex != null && locData != null)
                {
                    Objects = ObjectDefinitionSet.Parse(locIndex, locData);
                }

            }

            Log.Info("Cache", "Map regions: {0}, floors: {1}, objects: {2}.",
                Map.Count, Floors.Count, Objects.Count);
        }

        private static byte[] GetFromArchive(Archive archive, string name)
        {
            int index = archive.IndexOf(name);
            if (index < 0) return null;

            try
            {
                return archive.GetFile(index);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The underlying file store, for loaders that need raw access.
        /// </summary>
        /// <remarks>
        /// Exposed so the game layer's definition loader can read the config archives itself
        /// rather than this class duplicating every format it parses.
        /// </remarks>
        public FileStore Store
        {
            get { return _store; }
        }

        /// <summary>
        /// Loads a 64x64 region, or returns <c>null</c> when the cache does not have it.
        /// </summary>
        /// <remarks>
        /// Regions are cached: a player standing still asks for the same one every frame, and
        /// decompressing a landscape file is not cheap. The cache is bounded by
        /// <see cref="ClearRegions"/>, which the caller can call after a region change.
        /// </remarks>
        public Landscape TryGetRegion(int regionX, int regionY)
        {
            int key = (regionX << 8) | (regionY & 0xFF);

            if (key == _lastRegionKey)
            {
                Landscape fast = _lastRegion;
                if (fast != null) return fast;
            }

            lock (_sync)
            {
                Landscape cached;
                if (_regions.TryGetValue(key, out cached))
                {
                    _lastRegionKey = key;
                    _lastRegion = cached;
                    return cached;
                }
            }

            MapIndexEntry entry;
            if (!Map.TryGet(regionX, regionY, out entry)) return null;

            byte[] terrain = Gzip.Decompress(_store.Read(MapStore, entry.TerrainFileId));
            if (terrain == null) return null;

            byte[] objects = Gzip.Decompress(_store.Read(MapStore, entry.ObjectFileId));

            Landscape landscape = Landscape.Parse(terrain, objects, regionX, regionY);
            if (landscape == null) return null;

            lock (_sync)
            {
                // Bounded so a long session that walks the whole world does not hold every
                // region in memory for ever. Clearing wholesale is crude but the reload is a
                // decompress, not a network fetch.
                if (_regions.Count >= 64)
                {
                    _regions.Clear();
                    _lastRegion = null;
                    _lastRegionKey = -1;
                }

                _regions[key] = landscape;
            }

            _lastRegionKey = key;
            _lastRegion = landscape;

            return landscape;
        }

        /// <summary>Drops every cached region.</summary>
        public void ClearRegions()
        {
            lock (_sync)
            {
                _regions.Clear();
                _lastRegion = null;
                _lastRegionKey = -1;
            }
        }

        /// <summary>Releases the underlying files.</summary>
        public void Dispose()
        {
            if (_store != null) _store.Dispose();
        }
    }
}
