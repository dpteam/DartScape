using System;
using System.Collections.Generic;
using System.IO;
using DartScape.Core.Diagnostics;
using DartScape.Core.Protocol;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// Reader for the Jagex 317 file store (<c>main_file_cache.dat</c> plus
    /// <c>main_file_cache.idx0..idx4</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is a flat array of 520-byte sectors in <c>.dat</c>. Each sector
    /// starts with an 8-byte header: 2-byte file id, 2-byte part number, 3-byte
    /// "next sector" pointer and a 1-byte store id. The <c>.idxN</c> files hold one
    /// 6-byte entry per file: a 3-byte length and a 3-byte first sector.
    /// </para>
    /// <para>
    /// Reads are cached by (store, id) because the client asks for the same
    /// interfaces and models over and over, and a 520-byte-stride read per request
    /// is the classic reason a 317 client stutters when opening a bank.
    /// </para>
    /// </remarks>
    public sealed class FileStore : IDisposable
    {
        /// <summary>Bytes per sector in the data file.</summary>
        public const int SectorSize = 520;

        /// <summary>Payload bytes per sector (sector size minus the 8-byte header).</summary>
        public const int PayloadSize = SectorSize - 8;

        private readonly FileStream _data;
        private readonly FileStream[] _index;
        private readonly byte[] _sectorBuffer = new byte[SectorSize];
        private readonly Dictionary<long, byte[]> _cache = new Dictionary<long, byte[]>();
        private readonly object _sync = new object();
        private readonly StoreStyle[] _storeStyle = new StoreStyle[255];
        private int _cacheLimit = 4096;

        /// <summary>How a store numbers its sectors. Detected on first read.</summary>
        private enum StoreStyle
        {
            Unknown = 0,
            ZeroBased = 1,
            OneBased = 2
        }

        /// <summary>Directory the store was loaded from.</summary>
        public string Directory { get; private set; }

        /// <summary>Number of index stores (5 for a stock 317 cache: 0..4).</summary>
        public int StoreCount
        {
            get { return _index.Length; }
        }

        private FileStore(string directory, FileStream data, FileStream[] index)
        {
            Directory = directory;
            _data = data;
            _index = index;
        }

        /// <summary>
        /// Opens a cache directory. Returns <c>null</c> (with a logged reason) when the
        /// files are absent вЂ” the caller decides whether that is fatal, because the
        /// client can still show "no cache" and the server can run without one.
        /// </summary>
        public static FileStore Open(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException("directory");

            string dataPath = Path.Combine(directory, "main_file_cache.dat");
            if (!File.Exists(dataPath))
            {
                Log.Warning("Cache", "No main_file_cache.dat in {0}.", directory);
                return null;
            }

            try
            {
                var data = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

                var indices = new List<FileStream>();
                for (int i = 0; i < 255; i++)
                {
                    string idxPath = Path.Combine(directory, "main_file_cache.idx" + i);
                    if (!File.Exists(idxPath)) break;
                    indices.Add(new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024));
                }

                if (indices.Count == 0)
                {
                    data.Dispose();
                    Log.Warning("Cache", "No index files next to {0}.", dataPath);
                    return null;
                }

                var store = new FileStore(directory, data, indices.ToArray());
                Log.Info("Cache", "Opened {0}: {1} store(s), {2:N0} KB data.",
                    directory, indices.Count, data.Length / 1024);
                return store;
            }
            catch (Exception ex)
            {
                Log.Error("Cache", "Could not open cache in {0}: {1}", directory, ex.Message);
                return null;
            }
        }

        /// <summary>Number of files held in <paramref name="store"/>.</summary>
        public int GetFileCount(int store)
        {
            if (store < 0 || store >= _index.Length) return 0;
            lock (_sync)
            {
                return (int)(_index[store].Length / 6);
            }
        }

        /// <summary>
        /// Reads one file. Returns <c>null</c> when the id is out of range or the
        /// sector chain is corrupt вЂ” a corrupt cache must never throw into the
        /// render loop.
        /// </summary>
        public byte[] Read(int store, int fileId)
        {
            if (store < 0 || store >= _index.Length) return null;
            if (fileId < 0) return null;

            long cacheKey = ((long)store << 32) | (uint)fileId;
            lock (_sync)
            {
                byte[] cached;
                if (_cache.TryGetValue(cacheKey, out cached)) return cached;

                byte[] result = ReadUncached(store, fileId);
                if (result == null) return null;

                if (_cache.Count >= _cacheLimit) _cache.Clear();
                _cache[cacheKey] = result;
                return result;
            }
        }

        private byte[] ReadUncached(int store, int fileId)
        {
            FileStream indexFile = _index[store];
            long indexOffset = (long)fileId * 6;
            if (indexOffset + 6 > indexFile.Length) return null;

            var header = new byte[6];
            indexFile.Position = indexOffset;
            if (!ReadFully(indexFile, header, 6)) return null;

            int length = ((header[0] & 0xFF) << 16) | ((header[1] & 0xFF) << 8) | (header[2] & 0xFF);
            int sector = ((header[3] & 0xFF) << 16) | ((header[4] & 0xFF) << 8) | (header[5] & 0xFF);

            if (length <= 0 || sector <= 0) return null;

            long maxSector = _data.Length / SectorSize;
            if (sector > maxSector) return null;

            var result = new byte[length];
            int read = 0;
            int part = 0;
            int guard = 0;

            while (read < length)
            {
                if (sector <= 0 || sector > maxSector) return null;
                if (++guard > 0x10000) return null;   // cyclic chain guard

                _data.Position = (long)sector * SectorSize;
                if (!ReadFully(_data, _sectorBuffer, SectorSize)) return null;

                int chunkFileId = ((_sectorBuffer[0] & 0xFF) << 8) | (_sectorBuffer[1] & 0xFF);
                int chunkPart = ((_sectorBuffer[2] & 0xFF) << 8) | (_sectorBuffer[3] & 0xFF);
                int nextSector = ((_sectorBuffer[4] & 0xFF) << 16) | ((_sectorBuffer[5] & 0xFF) << 8) | (_sectorBuffer[6] & 0xFF);
                int chunkStore = _sectorBuffer[7] & 0xFF;

                // File id and part number are the real integrity check: a wrong value
                // means we followed a bad chain. The store byte is advisory, because
                // caches in the wild were written by tools that used either 0-based or
                // 1-based store numbering, and some leave it at zero. Rejecting on it
                // would refuse to load perfectly valid caches.
                if (chunkFileId != fileId || chunkPart != part)
                {
                    Log.Warning("Cache", "Corrupt sector chain at store {0} file {1} part {2} (saw file {3} part {4}).",
                        store, fileId, part, chunkFileId, chunkPart);
                    return null;
                }

                if (chunkStore != store && chunkStore != store + 1 && chunkStore != 0 && _storeStyle[store] == StoreStyle.Unknown)
                {
                    _storeStyle[store] = chunkStore == store + 1 ? StoreStyle.OneBased : StoreStyle.ZeroBased;
                    Log.Debug("Cache", "Store {0} uses {1} sector ids.", store, _storeStyle[store] == StoreStyle.OneBased ? "1-based" : "0-based");
                }

                int take = Math.Min(PayloadSize, length - read);
                Buffer.BlockCopy(_sectorBuffer, 8, result, read, take);
                read += take;
                sector = nextSector;
                part++;
            }

            return result;
        }

        private static bool ReadFully(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = stream.Read(buffer, offset, count - offset);
                if (n <= 0) return false;
                offset += n;
            }
            return true;
        }

        /// <summary>Drops the in-memory file cache.</summary>
        public void ClearCache()
        {
            lock (_sync)
            {
                _cache.Clear();
            }
        }

        /// <summary>Sets how many files stay cached. Zero disables caching.</summary>
        public void SetCacheLimit(int limit)
        {
            lock (_sync)
            {
                _cacheLimit = Math.Max(0, limit);
                if (_cacheLimit == 0) _cache.Clear();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _cache.Clear();
                try { _data.Dispose(); } catch (Exception) { }
                foreach (FileStream f in _index)
                {
                    try { f.Dispose(); } catch (Exception) { }
                }
            }
        }
    }

    /// <summary>
    /// A container inside a store file: a RuneScape "archive" holds several files
    /// addressable by name hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two layouts exist in the wild and both are supported here:
    /// </para>
    /// <para>
    /// <b>Legacy 317</b> вЂ” a 2-byte file count, then per file a 4-byte name hash,
    /// a 4-byte length and a 4-byte offset; then the bodies; then a tail of
    /// null-terminated names. Offsets are relative to the end of the header.
    /// </para>
    /// <para>
    /// <b>Modern (377+)</b> вЂ” a 3-byte decompressed length and a 3-byte stored
    /// length up front; when they differ the remainder is a bzip2 stream. After
    /// decompression: a 2-byte file count, then per file a 4-byte name hash, a
    /// 3-byte size and a 3-byte stored size (10 bytes); bodies follow immediately
    /// and there is no name table at all вЂ” lookup is by hash only.
    /// </para>
    /// <para>
    /// The format is detected rather than configured, because a cache directory
    /// arriving from anywhere may be either, and guessing wrong produces a very
    /// confusing "everything is garbage" failure.
    /// </para>
    /// </remarks>
    public sealed class Archive
    {
        private readonly Dictionary<int, int> _hashToIndex = new Dictionary<int, int>();
        private readonly int[] _hashes;
        private readonly int[] _lengths;
        private readonly int[] _stored;
        private readonly int[] _offsets;
        private readonly byte[] _data;
        private readonly int _bodyStart;
        private readonly string[] _names;

        /// <summary>Number of files in the archive.</summary>
        public int Count
        {
            get { return _hashes.Length; }
        }

        /// <summary>True when the archive carried a name table (legacy layout).</summary>
        public bool HasNames
        {
            get { return _names != null; }
        }

        /// <summary>True when the payload itself had to be bzip2-decompressed.</summary>
        public bool WasCompressed { get; private set; }

        private Archive(byte[] data, int[] hashes, int[] lengths, int[] stored, int[] offsets, int bodyStart, string[] names)
        {
            _data = data;
            _hashes = hashes;
            _lengths = lengths;
            _stored = stored;
            _offsets = offsets;
            _bodyStart = bodyStart;
            _names = names;

            for (int i = 0; i < hashes.Length; i++)
            {
                if (!_hashToIndex.ContainsKey(hashes[i])) _hashToIndex.Add(hashes[i], i);
            }
        }

        /// <summary>
        /// Parses an archive payload, transparently decompressing and detecting the
        /// layout. Returns <c>null</c> when the payload is not a recognisable archive.
        /// </summary>
        public static Archive Parse(byte[] payload)
        {
            if (payload == null || payload.Length < 6) return null;

            byte[] body = payload;
            bool compressed = false;

            // Modern header: two 3-byte lengths. Equal means "stored verbatim".
            int declaredLength = (payload[0] << 16) | (payload[1] << 8) | payload[2];
            int storedLength = (payload[3] << 16) | (payload[4] << 8) | payload[5];

            if (declaredLength != storedLength)
            {
                if (storedLength <= 0 || 6 + storedLength > payload.Length) return null;

                try
                {
                    body = Bzip2.DecompressJagex(payload, 6, storedLength, declaredLength);
                    compressed = true;
                }
                catch (Exception ex)
                {
                    Log.Warning("Cache", "bzip2 decompression failed: {0}", ex.Message);
                    return null;
                }
            }
            else if (declaredLength > 0 && declaredLength == payload.Length - 6)
            {
                // Explicitly "stored" вЂ” the body is everything after the 6-byte header.
                body = new byte[declaredLength];
                Buffer.BlockCopy(payload, 6, body, 0, declaredLength);
            }
            else
            {
                // Legacy layout has no length prefix at all: the first two bytes are
                // the file count. Nothing to strip.
                body = payload;
            }

            Archive result = ParseModern(body);
            if (result != null)
            {
                result.WasCompressed = compressed;
                return result;
            }

            result = ParseLegacy(body);
            if (result != null) result.WasCompressed = compressed;
            return result;
        }

        /// <summary>377+ layout: 2-byte count, 10-byte entries, no names.</summary>
        private static Archive ParseModern(byte[] body)
        {
            if (body.Length < 2) return null;

            int count = (body[0] << 8) | body[1];
            if (count <= 0 || count > 0x4000) return null;

            int headerEnd = 2 + count * 10;
            if (headerEnd > body.Length) return null;

            var hashes = new int[count];
            var lengths = new int[count];
            var stored = new int[count];
            var offsets = new int[count];

            int pos = 2;
            int running = 0;
            long total = 0;

            for (int i = 0; i < count; i++)
            {
                hashes[i] = (body[pos] << 24) | (body[pos + 1] << 16) | (body[pos + 2] << 8) | body[pos + 3];
                int size = (body[pos + 4] << 16) | (body[pos + 5] << 8) | body[pos + 6];
                int compressedSize = (body[pos + 7] << 16) | (body[pos + 8] << 8) | body[pos + 9];
                pos += 10;

                lengths[i] = size;
                stored[i] = compressedSize;
                offsets[i] = running;
                running += compressedSize;
                total += compressedSize;
            }

            if (headerEnd + total > body.Length) return null;

            for (int i = 0; i < count; i++)
            {
                if (lengths[i] < 0 || lengths[i] > 0x1000000) return null;
                if (stored[i] < 0 || stored[i] > 0x1000000) return null;
            }

            return new Archive(body, hashes, lengths, stored, offsets, headerEnd, null);
        }

        /// <summary>Legacy 317 layout: 2-byte count, 12-byte entries, name tail.</summary>
        private static Archive ParseLegacy(byte[] body)
        {
            if (body.Length < 6) return null;

            int count = (body[0] << 8) | body[1];
            if (count <= 0 || count > 0x4000) return null;

            int headerEnd = 2 + count * 12;
            if (headerEnd > body.Length) return null;

            var hashes = new int[count];
            var lengths = new int[count];
            var stored = new int[count];
            var offsets = new int[count];

            int pos = 2;
            for (int i = 0; i < count; i++)
            {
                hashes[i] = (body[pos] << 24) | (body[pos + 1] << 16) | (body[pos + 2] << 8) | body[pos + 3];
                lengths[i] = (body[pos + 4] << 24) | (body[pos + 5] << 16) | (body[pos + 6] << 8) | body[pos + 7];
                stored[i] = lengths[i];
                offsets[i] = (body[pos + 8] << 24) | (body[pos + 9] << 16) | (body[pos + 10] << 8) | body[pos + 11];
                pos += 12;

                if (lengths[i] < 0 || offsets[i] < 0) return null;
            }

            // The name table sits at the very end: count null-terminated strings.
            var names = new string[count];
            int cursor = body.Length;
            bool ok = true;

            for (int i = count - 1; i >= 0; i--)
            {
                int end = cursor;
                int start = end - 1;
                while (start > headerEnd && body[start] != 0) start--;
                if (start < headerEnd) { ok = false; break; }
                names[i] = TextEncoding.GetString(body, start + 1, end - start - 1);
                cursor = start;
            }

            int bodyStart = headerEnd;
            if (ok) bodyStart = Math.Max(headerEnd, cursor + 1);
            if (bodyStart + MaxEnd(lengths, offsets) > body.Length) return null;

            return new Archive(body, hashes, lengths, stored, offsets, bodyStart, ok ? names : null);
        }

        private static long MaxEnd(int[] lengths, int[] offsets)
        {
            long max = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                long end = (long)offsets[i] + lengths[i];
                if (end > max) max = end;
            }
            return max;
        }

        /// <summary>Name of the file at <paramref name="index"/>, or <c>null</c> in modern archives.</summary>
        public string GetName(int index)
        {
            if (_names == null || index < 0 || index >= _names.Length) return null;
            return _names[index];
        }

        /// <summary>All known file names, or an empty array for a nameless archive.</summary>
        public string[] GetNames()
        {
            if (_names == null) return new string[0];
            var copy = new string[_names.Length];
            Array.Copy(_names, copy, _names.Length);
            return copy;
        }

        /// <summary>Name hash of the file at <paramref name="index"/>.</summary>
        public int GetHash(int index)
        {
            if (index < 0 || index >= _hashes.Length) return 0;
            return _hashes[index];
        }

        /// <summary>Index for a name, or -1.</summary>
        public int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            int index;
            return _hashToIndex.TryGetValue(NameHash(name), out index) ? index : -1;
        }

        /// <summary>Body of the file at <paramref name="index"/>, or <c>null</c>.</summary>
        public byte[] GetFile(int index)
        {
            if (index < 0 || index >= _hashes.Length) return null;

            int length = _lengths[index];
            int storedLength = _stored[index];
            int offset = _bodyStart + _offsets[index];

            if (length < 0 || storedLength < 0 || offset < 0 || offset + storedLength > _data.Length) return null;

            // Individual entries may be bzip2-compressed inside an uncompressed archive.
            if (storedLength != length)
            {
                try
                {
                    return Bzip2.DecompressJagex(_data, offset, storedLength, length);
                }
                catch (Exception ex)
                {
                    Log.Warning("Cache", "bzip2 failed for archive entry {0}: {1}", index, ex.Message);
                    return null;
                }
            }

            var result = new byte[length];
            Buffer.BlockCopy(_data, offset, result, 0, length);
            return result;
        }

        /// <summary>Body of the file with the given name, or <c>null</c>.</summary>
        public byte[] GetFile(string name)
        {
            return GetFile(IndexOf(name));
        }

        /// <summary>
        /// The standard RuneScape name hash: <c>hash = hash * 61 + upper(c) - 32</c>.
        /// </summary>
        public static int NameHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;

            int hash = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= 'a' && c <= 'z') c = (char)(c - 32);
                hash = hash * 61 + c - 32;
            }
            return hash;
        }
    }

    /// <summary>Minimal big-endian reader used for cache parsing (kept local to avoid a cycle).</summary>
    internal sealed class PacketReader
    {
        private readonly byte[] _data;
        private int _pos;

        public PacketReader(byte[] data)
        {
            _data = data ?? new byte[0];
        }

        public int Position
        {
            get { return _pos; }
            set { _pos = value; }
        }

        public int Remaining
        {
            get { return _data.Length - _pos; }
        }

        public int ReadUnsignedByte()
        {
            if (_pos >= _data.Length) throw new InvalidDataException("Unexpected end of archive.");
            return _data[_pos++] & 0xFF;
        }

        public int ReadUnsignedShort()
        {
            return (ReadUnsignedByte() << 8) | ReadUnsignedByte();
        }

        public int ReadInt()
        {
            return (ReadUnsignedByte() << 24) | (ReadUnsignedByte() << 16) | (ReadUnsignedByte() << 8) | ReadUnsignedByte();
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0 || _pos + count > _data.Length) throw new InvalidDataException("Unexpected end of archive.");
            var result = new byte[count];
            Buffer.BlockCopy(_data, _pos, result, 0, count);
            _pos += count;
            return result;
        }
    }
}
