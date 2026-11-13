using System;
using System.IO;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// Pure managed bzip2 decompressor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RuneScape cache stores some archives bzip2-compressed, and .NET Framework
    /// 4.0 ships no bzip2 support (and no third-party libraries are allowed), so this
    /// is a from-scratch implementation of the reference algorithm.
    /// </para>
    /// <para>
    /// Only decompression exists вЂ” nothing in the project ever needs to write bzip2.
    /// The decoder is single-shot: give it a complete stream and the known output
    /// size, get the bytes back. That is exactly how the cache format presents it.
    /// </para>
    /// <para>
    /// The implementation is deliberately literal rather than clever. Correctness here
    /// matters far more than a few percent of speed, and the hot path вЂ” uncompressed
    /// archives вЂ” never enters this class at all.
    /// </para>
    /// </remarks>
    public static class Bzip2
    {
        private const int MaxGroups = 6;
        private const int MaxAlphaSize = 258;
        private const int GroupSize = 50;
        private const int MaxCodeLen = 23;
        private const int MaxSelectors = 2 + (900000 / GroupSize);

        /// <summary>
        /// Decompresses a RuneScape-style bzip2 payload, which omits the four-byte
        /// <c>BZh&lt;level&gt;</c> stream header and starts directly at the first block
        /// marker.
        /// </summary>
        /// <remarks>
        /// Every cache in the wild stores the compressed bytes this way вЂ” the header
        /// carries no information the decoder needs beyond an upper bound on the block
        /// size, and Jagex simply dropped it. Prepending a synthetic level-9 header is
        /// therefore exactly equivalent to having received a normal stream.
        /// </remarks>
        public static byte[] DecompressJagex(byte[] source, int offset, int length, int expectedLength)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (offset < 0 || length < 0 || offset + length > source.Length) throw new ArgumentOutOfRangeException("length");
            if (length == 0) return new byte[expectedLength];

            var framed = new byte[length + 4];
            framed[0] = (byte)'B';
            framed[1] = (byte)'Z';
            framed[2] = (byte)'h';
            framed[3] = (byte)'9';      // largest block size; only used as a buffer bound
            Buffer.BlockCopy(source, offset, framed, 4, length);

            return Decompress(framed, 0, framed.Length, expectedLength);
        }

        /// <summary>
        /// Decompresses <paramref name="source"/> into exactly
        /// <paramref name="expectedLength"/> bytes.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream is malformed.</exception>
        public static byte[] Decompress(byte[] source, int offset, int length, int expectedLength)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (expectedLength < 0) throw new ArgumentOutOfRangeException("expectedLength");
            if (offset < 0 || length < 0 || offset + length > source.Length) throw new ArgumentOutOfRangeException("length");

            var output = new byte[expectedLength];
            if (expectedLength == 0) return output;

            var input = new BitReader(source, offset, length);

            if (input.ReadByte() != 'B' || input.ReadByte() != 'Z' || input.ReadByte() != 'h')
            {
                throw new InvalidDataException("Not a bzip2 stream (missing BZh header).");
            }

            int level = input.ReadByte() - '0';
            if (level < 1 || level > 9) throw new InvalidDataException("Invalid bzip2 block size: " + level);

            int maxBlockBytes = level * 100000;
            int written = 0;

            while (true)
            {
                uint magicHi = input.ReadBits(24);
                uint magicLo = input.ReadBits(24);

                if (magicHi == 0x177245 && magicLo == 0x385090)
                {
                    break;      // end-of-stream marker
                }

                if (magicHi != 0x314159 || magicLo != 0x265359)
                {
                    throw new InvalidDataException("Bad bzip2 block marker.");
                }

                written = DecodeBlock(input, output, written, maxBlockBytes);
            }

            if (written != expectedLength)
            {
                throw new InvalidDataException(string.Format(
                    "bzip2 produced {0} bytes but {1} were expected.", written, expectedLength));
            }

            return output;
        }

        private static int DecodeBlock(BitReader input, byte[] output, int written, int maxBlockBytes)
        {
            input.ReadBits(32);                             // block CRC (not verified)
            bool randomised = input.ReadBits(1) == 1;
            if (randomised)
            {
                // Deprecated since bzip2 0.9.5; no real encoder emits it, and faking
                // the table would silently produce garbage.
                throw new InvalidDataException("Randomised bzip2 blocks are not supported.");
            }

            int origPtr = (int)input.ReadBits(24);

            // --- symbol map -------------------------------------------------------
            var seqToUnseq = new byte[256];
            int nInUse = 0;

            int used16 = (int)input.ReadBits(16);
            for (int i = 0; i < 16; i++)
            {
                if ((used16 & (0x8000 >> i)) != 0)
                {
                    int bits = (int)input.ReadBits(16);
                    for (int j = 0; j < 16; j++)
                    {
                        if ((bits & (0x8000 >> j)) != 0) seqToUnseq[nInUse++] = (byte)(i * 16 + j);
                    }
                }
            }

            if (nInUse == 0) throw new InvalidDataException("bzip2 block uses no symbols.");

            int alphaSize = nInUse + 2;                     // + RUNA, RUNB

            // --- Huffman group selection ------------------------------------------
            int nGroups = (int)input.ReadBits(3);
            if (nGroups < 2 || nGroups > MaxGroups) throw new InvalidDataException("Invalid bzip2 group count.");

            int nSelectors = (int)input.ReadBits(15);
            if (nSelectors < 1 || nSelectors > MaxSelectors) throw new InvalidDataException("Invalid bzip2 selector count.");
            BzTrace("nInUse={0} nGroups={1} nSelectors={2} origPtr={3}", nInUse, nGroups, nSelectors, origPtr);

            var selectorMtf = new byte[nSelectors];
            for (int i = 0; i < nSelectors; i++)
            {
                int j = 0;
                while (input.ReadBits(1) == 1)
                {
                    j++;
                    if (j >= nGroups) throw new InvalidDataException("bzip2 selector MTF overflow.");
                }
                selectorMtf[i] = (byte)j;
            }

            var selectors = new byte[nSelectors];
            var groupOrder = new byte[MaxGroups];
            for (int i = 0; i < nGroups; i++) groupOrder[i] = (byte)i;

            for (int i = 0; i < nSelectors; i++)
            {
                int v = selectorMtf[i];
                byte tmp = groupOrder[v];
                for (int k = v; k > 0; k--) groupOrder[k] = groupOrder[k - 1];
                groupOrder[0] = tmp;
                selectors[i] = tmp;
            }

            // --- code lengths ------------------------------------------------------
            var codeLengths = new byte[MaxGroups][];
            for (int t = 0; t < nGroups; t++)
            {
                codeLengths[t] = new byte[MaxAlphaSize];
                int curr = (int)input.ReadBits(5);
                for (int i = 0; i < alphaSize; i++)
                {
                    // Delta-coded: each step is either a single 0 bit (done) or a
                    // two-bit "10" (length + 1) / "11" (length - 1) pair.
                    while (true)
                    {
                        if (input.ReadBits(1) == 0) break;
                        if (input.ReadBits(1) == 1) curr--;
                        else curr++;
                    }
                    codeLengths[t][i] = (byte)curr;
                }
            }

            var limit = new int[MaxGroups][];
            var baseArray = new int[MaxGroups][];
            var perm = new int[MaxGroups][];
            var minLens = new int[MaxGroups];
            var permCount = new int[MaxGroups];

            for (int t = 0; t < nGroups; t++)
            {
                limit[t] = new int[MaxCodeLen + 2];
                baseArray[t] = new int[MaxCodeLen + 2];
                perm[t] = new int[MaxAlphaSize];
                permCount[t] = CreateDecodeTables(limit[t], baseArray[t], perm[t], codeLengths[t], alphaSize, out minLens[t]);
            }

            // --- main symbol loop --------------------------------------------------
            var block = new byte[maxBlockBytes];
            int blockLen = 0;

            int groupIndex = -1;
            int groupPos = 0;
            int currentGroup = 0;

            var mtf = new byte[256];
            for (int i = 0; i < nInUse; i++) mtf[i] = (byte)i;

            int runLength = 0;
            int runWeight = 1;

            while (true)
            {
                if (groupPos == 0)
                {
                    groupIndex++;
                    if (groupIndex >= nSelectors) throw new InvalidDataException("bzip2 selector exhausted.");
                    currentGroup = selectors[groupIndex];
                    groupPos = GroupSize;
                }
                groupPos--;

                int sym = DecodeSymbol(input, limit[currentGroup], baseArray[currentGroup],
                    perm[currentGroup], permCount[currentGroup], minLens[currentGroup]);
                if (sym < 0) throw new InvalidDataException("bzip2 Huffman decode failed.");

                // The alphabet is: 0 = RUNA, 1 = RUNB, 2..nInUse = MTF index 1..nInUse-1,
                // nInUse+1 = end of block. A single zero MTF value never appears on its
                // own, because the encoder always run-length encodes it.
                if (sym == nInUse + 1) break;

                if (sym <= 1)
                {
                    runLength += sym == 0 ? runWeight : 2 * runWeight;
                    runWeight <<= 1;
                    if (runLength > block.Length) throw new InvalidDataException("bzip2 run too long.");
                    continue;
                }

                if (runLength > 0)
                {
                    byte runByte = seqToUnseq[mtf[0]];
                    if (blockLen + runLength > block.Length) throw new InvalidDataException("bzip2 block overflow.");
                    for (int i = 0; i < runLength; i++) block[blockLen++] = runByte;
                    runLength = 0;
                    runWeight = 1;
                }

                int idx = sym - 1;
                if (idx >= nInUse) throw new InvalidDataException("bzip2 symbol out of range.");

                byte value = mtf[idx];
                Array.Copy(mtf, 0, mtf, 1, idx);
                mtf[0] = value;

                if (blockLen >= block.Length) throw new InvalidDataException("bzip2 block overflow.");
                block[blockLen++] = seqToUnseq[value];
            }

            if (runLength > 0)
            {
                byte runByte = seqToUnseq[mtf[0]];
                if (blockLen + runLength > block.Length) throw new InvalidDataException("bzip2 block overflow.");
                for (int i = 0; i < runLength; i++) block[blockLen++] = runByte;
            }

            return InverseBwtAndExpand(block, blockLen, origPtr, output, written);
        }

        /// <summary>
        /// Builds the T vector, walks it in original order, and undoes the RLE1 pass.
        /// </summary>
        private static int InverseBwtAndExpand(byte[] block, int length, int origPtr, byte[] output, int written)
        {
            if (length <= 0) return written;
            if (origPtr < 0 || origPtr >= length) throw new InvalidDataException("bzip2 origPtr out of range.");

            // T vector: tt[sortedPosition] = index in block.
            var cumulative = new int[256];
            for (int i = 0; i < length; i++) cumulative[block[i]]++;
            for (int i = 1; i < 256; i++) cumulative[i] += cumulative[i - 1];

            var tt = new int[length];
            for (int i = length - 1; i >= 0; i--)
            {
                int c = block[i];
                cumulative[c]--;
                tt[cumulative[c]] = i;
            }

            int outPos = written;
            int last = -1;
            int runCount = 0;

            int tPos = tt[origPtr];

            for (int n = 0; n < length; n++)
            {
                byte b = block[tPos];
                tPos = tt[tPos];

                // RLE1: four equal bytes are followed by a count of extra copies.
                if (runCount == 4)
                {
                    int extra = b & 0xFF;
                    if (outPos + extra > output.Length) throw new InvalidDataException("bzip2 output overflow.");
                    byte prev = output[outPos - 1];
                    for (int k = 0; k < extra; k++) output[outPos++] = prev;
                    runCount = 0;
                    last = -1;
                    continue;
                }

                if (outPos >= output.Length) throw new InvalidDataException("bzip2 output overflow.");
                output[outPos++] = b;

                if (last == b) runCount++;
                else { runCount = 1; last = b; }
            }

            return outPos;
        }

        /// <summary>Builds the canonical Huffman decode tables. Returns the number of used symbols.</summary>
        private static int CreateDecodeTables(int[] limit, int[] baseArray, int[] perm, byte[] lengths, int alphaSize, out int minLen)
        {
            minLen = 32;
            int maxLen = 0;

            for (int i = 0; i < baseArray.Length; i++) baseArray[i] = 0;
            for (int i = 0; i < alphaSize; i++) baseArray[lengths[i] + 1]++;

            for (int i = 1; i < baseArray.Length; i++) baseArray[i] += baseArray[i - 1];

            for (int i = 0; i < alphaSize; i++)
            {
                int len = lengths[i];
                if (len <= 0) continue;
                if (len < minLen) minLen = len;
                if (len > maxLen) maxLen = len;
            }

            if (maxLen == 0) throw new InvalidDataException("bzip2 Huffman table is empty.");

            for (int i = 0; i < limit.Length; i++) limit[i] = 0;

            int vec = 0;
            for (int i = minLen; i <= maxLen; i++)
            {
                vec += baseArray[i + 1] - baseArray[i];
                limit[i] = vec - 1;
                vec <<= 1;
            }

            for (int i = minLen + 1; i <= maxLen; i++)
            {
                baseArray[i] = ((limit[i - 1] + 1) << 1) - baseArray[i];
            }

            // Symbols ordered by code length, then by symbol value.
            int pp = 0;
            for (int len = minLen; len <= maxLen; len++)
            {
                for (int i = 0; i < alphaSize; i++)
                {
                    if (lengths[i] == len) perm[pp++] = i;
                }
            }

            return pp;
        }

        private static int DecodeSymbol(BitReader input, int[] limit, int[] baseArray, int[] perm, int permCount, int minLen)
        {
            int zn = minLen;
            int zvec = (int)input.ReadBits(zn);

            while (true)
            {
                if (zn > MaxCodeLen) return -1;

                if (zvec <= limit[zn])
                {
                    int index = zvec - baseArray[zn];
                    if (index < 0 || index >= permCount) return -1;
                    return perm[index];
                }

                zn++;
                zvec = (zvec << 1) | (int)input.ReadBits(1);
            }
        }

        private static void BzTrace(string format, params object[] args)
        {
            if (Environment.GetEnvironmentVariable("DARTSCAPE_BZDEBUG") == null) return;
            Console.Error.WriteLine("bz: " + string.Format(format, args));
        }

        /// <summary>MSB-first bit reader over a byte slice.</summary>
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private readonly int _end;
            private int _pos;
            private uint _bitBuffer;
            private int _bitCount;

            public BitReader(byte[] data, int offset, int length)
            {
                _data = data;
                _pos = offset;
                _end = offset + length;
            }

            public int ReadByte()
            {
                if (_pos >= _end) throw new InvalidDataException("Unexpected end of bzip2 stream.");
                return _data[_pos++] & 0xFF;
            }

            public uint ReadBits(int count)
            {
                if (count == 0) return 0;
                if (count > 32) throw new ArgumentOutOfRangeException("count");

                while (_bitCount < count)
                {
                    if (_pos >= _end) throw new InvalidDataException("Unexpected end of bzip2 stream.");
                    _bitBuffer = (_bitBuffer << 8) | (uint)(_data[_pos++] & 0xFF);
                    _bitCount += 8;
                }

                _bitCount -= count;
                uint mask = count == 32 ? 0xFFFFFFFFu : ((1u << count) - 1u);
                uint value = (_bitBuffer >> _bitCount) & mask;
                _bitBuffer &= _bitCount == 32 ? 0xFFFFFFFFu : ((1u << _bitCount) - 1u);
                return value;
            }
        }
    }
}
