using System;
using System.Text;

namespace DartScape.Core.Cache
{
    /// <summary>
    /// Big-endian byte reader for cache payloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every format in the cache is big-endian and none of them are self-describing, so a
    /// reader that silently returns zero past the end is worse than useless: it turns a wrong
    /// offset into a plausible-looking definition instead of an exception. This one throws.
    /// </para>
    /// <para>
    /// The names deliberately match the original's (<c>getUnsignedByte</c>, <c>getSmartB</c>)
    /// so the ported parsers can be compared against the reference line by line.
    /// </para>
    /// </remarks>
    public sealed class CacheBuffer
    {
        private readonly byte[] _data;
        private int _position;

        public CacheBuffer(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            _data = data;
        }

        /// <summary>Current read offset.</summary>
        public int Position
        {
            get { return _position; }
            set
            {
                if (value < 0 || value > _data.Length)
                {
                    throw new ArgumentOutOfRangeException("value", "Position outside the buffer.");
                }

                _position = value;
            }
        }

        /// <summary>Total length of the buffer.</summary>
        public int Length
        {
            get { return _data.Length; }
        }

        /// <summary>Bytes left to read.</summary>
        public int Remaining
        {
            get { return _data.Length - _position; }
        }

        private void Require(int count)
        {
            if (_position + count > _data.Length)
            {
                throw new InvalidOperationException(
                    "Cache payload ended at " + _position + " while " + count + " more bytes were needed.");
            }
        }

        /// <summary>Reads one unsigned byte.</summary>
        public int GetUnsignedByte()
        {
            Require(1);
            return _data[_position++] & 0xFF;
        }

        /// <summary>Reads one signed byte.</summary>
        public int GetByte()
        {
            Require(1);
            return (sbyte)_data[_position++];
        }

        /// <summary>Reads a big-endian unsigned short.</summary>
        public int GetUnsignedShort()
        {
            Require(2);
            int value = ((_data[_position] & 0xFF) << 8) | (_data[_position + 1] & 0xFF);
            _position += 2;
            return value;
        }

        /// <summary>Reads a big-endian signed short.</summary>
        public int GetShort()
        {
            int value = GetUnsignedShort();
            return value > 0x7FFF ? value - 0x10000 : value;
        }

        /// <summary>Reads a big-endian 32-bit integer.</summary>
        public int GetInt()
        {
            Require(4);
            int value = ((_data[_position] & 0xFF) << 24)
                      | ((_data[_position + 1] & 0xFF) << 16)
                      | ((_data[_position + 2] & 0xFF) << 8)
                      | (_data[_position + 3] & 0xFF);
            _position += 4;
            return value;
        }

        /// <summary>Reads a 24-bit big-endian value, used for packed RGB colours.</summary>
        public int Get3Bytes()
        {
            Require(3);
            int value = ((_data[_position] & 0xFF) << 16)
                      | ((_data[_position + 1] & 0xFF) << 8)
                      | (_data[_position + 2] & 0xFF);
            _position += 3;
            return value;
        }

        /// <summary>Reads the rest of the buffer.</summary>
        public byte[] GetRemaining()
        {
            var result = new byte[Remaining];
            Array.Copy(_data, _position, result, 0, result.Length);
            _position = _data.Length;
            return result;
        }

        /// <summary>
        /// Reads a cache string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The terminator is <c>10</c> — a newline — not zero. This is not a quirk of one
        /// file: every name, description and menu option in the cache ends this way, and the
        /// zero that follows is the terminator of the <em>record</em> that contained the
        /// string.
        /// </para>
        /// <para>
        /// Stopping at zero instead consumes that record terminator, so the parser walks
        /// straight into the next definition and keeps going until it happens to hit a zero
        /// that nothing else claimed. The result is not an error: it is one enormous
        /// definition built from the tail of the file, with the last values it read. That is
        /// how a whole floor table can parse "successfully" into two entries.
        /// </para>
        /// </remarks>
        public string GetString()
        {
            var builder = new StringBuilder(32);

            while (true)
            {
                Require(1);
                int value = _data[_position++] & 0xFF;
                if (value == 10) break;
                builder.Append((char)value);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Reads a "smart" value: one byte when it fits in seven bits, two otherwise.
        /// </summary>
        /// <remarks>
        /// This is the variant object blocks use. The one-byte form is unsigned; the two-byte
        /// form is a big-endian short with 0x8000 subtracted, so it can be negative.
        /// </remarks>
        public int GetSmart()
        {
            int peek = _data[_position] & 0xFF;
            return peek < 128 ? GetUnsignedByte() : GetUnsignedShort() - 0x8000;
        }

        /// <summary>
        /// Reads the signed smart value used for model vertex deltas.
        /// </summary>
        /// <remarks>
        /// Same shape as <see cref="GetSmart"/> but offset differently in both branches: minus
        /// 64 for the one-byte form and minus 0xC000 for the two-byte form. Model data is full
        /// of these and they are genuine signed deltas, so the offsets are load-bearing.
        ///
        /// This is <em>not</em> interchangeable with <see cref="GetSmart"/>. Using it on an
        /// object block desynchronises the whole block after the first entry: the object ids
        /// walk backwards into negative numbers and every position collapses to the corner of
        /// the region. The parse "succeeds" and produces nonsense.
        /// </remarks>
        public int GetSmartB()
        {
            int peek = _data[_position] & 0xFF;
            return peek < 128 ? GetUnsignedByte() - 64 : GetUnsignedShort() - 0xC000;
        }
    }
}
