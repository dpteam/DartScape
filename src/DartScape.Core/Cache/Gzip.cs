using System;
using DartScape.Core.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// Inflates the gzip payloads the cache stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 317 cache compresses most files with gzip (magic <c>1F 8B</c>), which
    /// <see cref="GZipStream"/> in the framework decodes. The other compressor in the cache,
    /// bzip2, is handled by <see cref="Bzip2"/>; which one a file uses is detected from its
    /// first two bytes rather than assumed from its location.
    /// </para>
    /// <para>
    /// A corrupt file returns <c>null</c> rather than throwing. The cache is third-party data
    /// that ships with the game and may be from any revision; one bad landscape file must not
    /// take the client down, and the caller can fall back to procedural terrain.
    /// </para>
    /// </remarks>
    public static class Gzip
    {
        /// <summary>True when the data starts with the gzip magic bytes.</summary>
        public static bool IsGzip(byte[] data)
        {
            return data != null && data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B;
        }

        /// <summary>True when the data starts with the bzip2 magic bytes.</summary>
        public static bool IsBzip2(byte[] data)
        {
            return data != null && data.Length > 3 && data[0] == 'B' && data[1] == 'Z' && data[2] == 'h';
        }

        /// <summary>
        /// Decompresses a cache file, detecting the compressor from the header.
        /// </summary>
        /// <returns>The plain bytes, or <c>null</c> when the file is corrupt.</returns>
        /// <remarks>
        /// There are three cases, and the middle one is the awkward one: Jagex's own bzip2
        /// streams have had their <c>BZh</c> header stripped and the uncompressed length moved
        /// to the last four bytes of the file. That is not a bzip2 stream by any standard
        /// reading, which is why <see cref="Bzip2.DecompressJagex"/> exists.
        /// </remarks>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            if (IsGzip(data)) return Inflate(data);

            if (IsBzip2(data))
            {
                try
                {
                    // A stream that still has its header carries no length, so it cannot be
                    // decoded without guessing. Nothing in the 317 cache looks like this.
                    Log.Debug("Cache", "Bzip2 file with an intact header; length is unknown.");
                    return null;
                }
                catch (Exception) { return null; }
            }

            if (IsJagexBzip2(data))
            {
                try
                {
                    int length = (data[data.Length - 4] << 24) | (data[data.Length - 3] << 16)
                               | (data[data.Length - 2] << 8) | data[data.Length - 1];

                    if (length < 0) return null;

                    return Bzip2.DecompressJagex(data, 0, data.Length - 4, length);
                }
                catch (Exception ex)
                {
                    Log.Debug("Cache", "Jagex bzip2 payload could not be inflated: {0}", ex.Message);
                    return null;
                }
            }

            // Already plain. Landscape files in some revisions are stored uncompressed.
            return data;
        }

        /// <summary>
        /// True when the data looks like a bzip2 stream with its header removed, which is how
        /// the cache stores compressed files: the block magic is left, the <c>BZh</c> is not.
        /// </summary>
        public static bool IsJagexBzip2(byte[] data)
        {
            return data != null && data.Length > 8
                && data[0] == 0x31 && data[1] == 0x41 && data[2] == 0x59 && data[3] == 0x26
                && data[4] == 0x53 && data[5] == 0x59;
        }

        /// <summary>Inflates a gzip stream.</summary>
        public static byte[] Inflate(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            try
            {
                using (var input = new MemoryStream(data, false))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream(data.Length * 4))
                {
                    var buffer = new byte[16 * 1024];
                    int read;

                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Cache", "Gzip payload could not be inflated: {0}", ex.Message);
                return null;
            }
        }
    }
}
