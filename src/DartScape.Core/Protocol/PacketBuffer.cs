using System;
using System.Text;

namespace DartScape.Core.Protocol
{
    /// <summary>
    /// Growable read/write byte buffer used by every packet in the protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the moral equivalent of the original <c>Stream</c> /
    /// <c>PacketBuilder</c> classes: one class that both ends of a connection use, so
    /// there is exactly one implementation of "write a smart int" to keep in sync.
    /// </para>
    /// <para>
    /// It is deliberately not thread-safe вЂ” a buffer belongs to one connection and is
    /// touched by one thread at a time. Sharing one across threads is a bug, and
    /// paying for a lock on every <c>WriteByte</c> in the hottest code path in the
    /// server would be silly.
    /// </para>
    /// <para>
    /// Reads are bounds-checked and throw <see cref="ProtocolException"/>, never
    /// <see cref="IndexOutOfRangeException"/>. A malicious client that lies about
    /// payload length gets a clean disconnect instead of a crash.
    /// </para>
    /// </remarks>
    public sealed class PacketBuffer
    {
        private byte[] _data;
        private int _readPos;
        private int _writePos;
        private int _bitPos;
        private int _readBitPos;

        /// <summary>Creates a buffer with the given initial capacity.</summary>
        public PacketBuffer(int capacity = 256)
        {
            if (capacity <= 0) capacity = 256;
            _data = new byte[capacity];
        }

        /// <summary>Creates a buffer over an existing array, ready to read from offset 0.</summary>
        public PacketBuffer(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            _data = data;
            _writePos = data.Length;
        }

        /// <summary>Creates a buffer over a slice of an existing array.</summary>
        public PacketBuffer(byte[] data, int offset, int length)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (offset < 0 || length < 0 || offset + length > data.Length)
                throw new ArgumentOutOfRangeException("length");

            _data = data;
            _readPos = offset;
            _writePos = offset + length;
        }

        /// <summary>The underlying array. Never assume it is exactly the payload length.</summary>
        public byte[] RawArray
        {
            get { return _data; }
        }

        /// <summary>Current read cursor.</summary>
        public int ReadPosition
        {
            get { return _readPos; }
            set
            {
                if (value < 0 || value > _writePos) throw new ProtocolException("Read position out of range.");
                _readPos = value;
            }
        }

        /// <summary>Current write cursor (= number of bytes written).</summary>
        public int WritePosition
        {
            get { return _writePos; }
        }

        /// <summary>Bytes available to read.</summary>
        public int Remaining
        {
            get { return _writePos - _readPos; }
        }

        /// <summary>Total capacity of the backing array.</summary>
        public int Capacity
        {
            get { return _data.Length; }
        }

        /// <summary>Resets both cursors. The backing array is kept for reuse.</summary>
        public void Reset()
        {
            _readPos = 0;
            _writePos = 0;
            _bitPos = 0;
        }

        // -- writing ----------------------------------------------------------

        private void EnsureCapacity(int extra)
        {
            int needed = _writePos + extra;
            if (needed <= _data.Length) return;

            int size = _data.Length;
            while (size < needed) size = size < 1024 ? size * 2 : size + size / 2;

            var grown = new byte[size];
            Buffer.BlockCopy(_data, 0, grown, 0, _writePos);
            _data = grown;
        }

        public PacketBuffer WriteByte(int value)
        {
            EnsureCapacity(1);
            _data[_writePos++] = (byte)value;
            return this;
        }

        /// <summary>Writes the low byte without the 0xFF mask вЂ” same as <see cref="WriteByte"/>.</summary>
        /// <summary>
        /// Writes <c>value + 128</c>, the 317 "byte A" (add) form.
        /// </summary>
        /// <remarks>
        /// This must be the inverse of <see cref="ReadUnsignedByteA"/>, which subtracts
        /// 128. Writing the plain value here made every skill level read back 128 higher
        /// than it should be — a level 1 stat displayed as 129, and a combat level of 0
        /// because the derived formula then produced nonsense.
        /// </remarks>
        public PacketBuffer WriteByteA(int value)
        {
            return WriteByte(value + 128);
        }

        /// <summary>Writes <c>value + 128</c>, the "offset byte" form used all over 317.</summary>
        public PacketBuffer WriteByteS(int value)
        {
            return WriteByte(value + 128);
        }

        /// <summary>Writes <c>-value</c>, the "negative byte" form.</summary>
        public PacketBuffer WriteByteC(int value)
        {
            return WriteByte(-value);
        }

        public PacketBuffer WriteBytes(byte[] source, int offset, int length)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (offset < 0 || length < 0 || offset + length > source.Length)
                throw new ArgumentOutOfRangeException("length");

            EnsureCapacity(length);
            Buffer.BlockCopy(source, offset, _data, _writePos, length);
            _writePos += length;
            return this;
        }

        public PacketBuffer WriteBytes(byte[] source)
        {
            if (source == null) throw new ArgumentNullException("source");
            return WriteBytes(source, 0, source.Length);
        }

        public PacketBuffer WriteShort(int value)
        {
            EnsureCapacity(2);
            _data[_writePos++] = (byte)(value >> 8);
            _data[_writePos++] = (byte)value;
            return this;
        }

        /// <summary>Little-endian 16-bit, used by the update blocks.</summary>
        public PacketBuffer WriteShortLE(int value)
        {
            EnsureCapacity(2);
            _data[_writePos++] = (byte)value;
            _data[_writePos++] = (byte)(value >> 8);
            return this;
        }

        /// <summary>Big-endian short with the bytes swapped: <c>A B</c> becomes <c>B+128 A+128</c>.</summary>
        public PacketBuffer WriteShortA(int value)
        {
            EnsureCapacity(2);
            _data[_writePos++] = (byte)(value >> 8);
            _data[_writePos++] = (byte)(value + 128);
            return this;
        }

        /// <summary>
        /// Little-endian short with the low byte offset by 128 (<c>putLEShortA</c>).
        /// </summary>
        /// <remarks>
        /// This is the encoding the original uses for the anchor X of a walk packet, and it is
        /// easy to confuse with <see cref="WriteShortA"/>: both offset one byte by 128, but they
        /// put the bytes in the opposite order. Reading one as the other yields a coordinate that
        /// is wrong by a few hundred tiles and gives no error.
        /// </remarks>
        public PacketBuffer WriteShortLEA(int value)
        {
            EnsureCapacity(2);
            _data[_writePos++] = (byte)(value + 128);
            _data[_writePos++] = (byte)(value >> 8);
            return this;
        }

        public PacketBuffer WriteInt(int value)
        {
            EnsureCapacity(4);
            _data[_writePos++] = (byte)(value >> 24);
            _data[_writePos++] = (byte)(value >> 16);
            _data[_writePos++] = (byte)(value >> 8);
            _data[_writePos++] = (byte)value;
            return this;
        }

        /// <summary>Middle-endian int: <c>A B C D</c> becomes <c>B A D C</c>. Used for map region hashes.</summary>
        public PacketBuffer WriteIntME(int value)
        {
            EnsureCapacity(4);
            _data[_writePos++] = (byte)(value >> 16);
            _data[_writePos++] = (byte)(value >> 24);
            _data[_writePos++] = (byte)value;
            _data[_writePos++] = (byte)(value >> 8);
            return this;
        }

        /// <summary>Little-endian int.</summary>
        public PacketBuffer WriteIntLE(int value)
        {
            EnsureCapacity(4);
            _data[_writePos++] = (byte)value;
            _data[_writePos++] = (byte)(value >> 8);
            _data[_writePos++] = (byte)(value >> 16);
            _data[_writePos++] = (byte)(value >> 24);
            return this;
        }

        public PacketBuffer WriteLong(long value)
        {
            EnsureCapacity(8);
            _data[_writePos++] = (byte)(value >> 56);
            _data[_writePos++] = (byte)(value >> 48);
            _data[_writePos++] = (byte)(value >> 40);
            _data[_writePos++] = (byte)(value >> 32);
            _data[_writePos++] = (byte)(value >> 24);
            _data[_writePos++] = (byte)(value >> 16);
            _data[_writePos++] = (byte)(value >> 8);
            _data[_writePos++] = (byte)value;
            return this;
        }

        /// <summary>
        /// Writes a 317 "smart" value: 1 byte when it fits in a signed byte,
        /// otherwise a marker byte plus a 16-bit value.
        /// </summary>
        public PacketBuffer WriteSmart(int value)
        {
            if (value >= 0 && value < 128) return WriteByte(value);
            if (value >= 0 && value < 32768) return WriteShort(value + 32768);
            throw new ProtocolException("Smart value out of range: " + value);
        }

        /// <summary>
        /// Writes a "smart" value the way the 317 client reads interface ids:
        /// two bytes when the high byte is non-zero, one byte otherwise.
        /// </summary>
        public PacketBuffer WriteSmartA(int value)
        {
            if (value >= 32768) return WriteShort(value - 32768);
            return WriteByte(value);
        }

        /// <summary>Writes a null-terminated ASCII/CP1252 string.</summary>
        public PacketBuffer WriteString(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                byte[] bytes = TextEncoding.GetBytes(value);
                WriteBytes(bytes, 0, bytes.Length);
            }
            return WriteByte(0);
        }

        /// <summary>Writes exactly <paramref name="length"/> bytes of a string, zero padded or truncated.</summary>
        public PacketBuffer WriteFixedString(string value, int length)
        {
            byte[] bytes = TextEncoding.GetBytes(value ?? string.Empty);
            int count = Math.Min(bytes.Length, length);
            WriteBytes(bytes, 0, count);
            for (int i = count; i < length; i++) WriteByte(0);
            return this;
        }

        /// <summary>Writes a 317 "long name" (base-37 packed string, max 12 chars).</summary>
        public PacketBuffer WriteName(string name)
        {
            if (name == null) name = string.Empty;
            if (name.Length > 12) name = name.Substring(0, 12);

            long encoded = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                encoded *= 37;
                if (c >= 'A' && c <= 'Z') encoded += c - 'A' + 1;
                else if (c >= 'a' && c <= 'z') encoded += c - 'a' + 1;
                else if (c >= '0' && c <= '9') encoded += c - '0' + 27;
                // anything else contributes nothing, matching the original client
            }

            // Variable length: 1..9 groups of 7 bits, most significant first.
            int groups = 1;
            for (int shift = 8; shift >= 1; shift--)
            {
                if (((encoded >> (shift * 7)) & 0x7FL) != 0) { groups = shift + 1; break; }
            }

            for (int shift = groups - 1; shift >= 0; shift--)
            {
                WriteByte((int)((encoded >> (shift * 7)) & 0x7F));
            }

            return this;
        }

        // -- bit access -------------------------------------------------------

        /// <summary>Starts bit-level writing; subsequent writes append bits LSB-first.</summary>
        public void StartBitAccess()
        {
            _bitPos = _writePos * 8;
        }

        /// <summary>Finishes bit-level writing and advances the byte cursor.</summary>
        public void FinishBitAccess()
        {
            _writePos = (_bitPos + 7) / 8;
            _bitPos = 0;
        }

        /// <summary>Writes <paramref name="count"/> bits (max 32) at the current bit position.</summary>
        public void WriteBits(int count, int value)
        {
            if (count <= 0 || count > 32) throw new ProtocolException("Bit count out of range: " + count);

            // Simple and obviously correct: walk the bits most-significant first and set
            // them one at a time. The packet writer is not the hot path (the tick loop
            // writes a few hundred bits per player per tick), and a subtle bug in a
            // word-at-a-time implementation costs far more than it saves.
            EnsureCapacity((_bitPos + count + 7) / 8 - _writePos);

            for (int i = count - 1; i >= 0; i--)
            {
                int byteIndex = _bitPos >> 3;
                int bitIndex = 7 - (_bitPos & 7);

                if (((value >> i) & 1) != 0) _data[byteIndex] |= (byte)(1 << bitIndex);
                else _data[byteIndex] &= (byte)~(1 << bitIndex);

                _bitPos++;
            }

            if (_bitPos > _writePos * 8) _writePos = (_bitPos + 7) / 8;
        }

        // -- bit reading ------------------------------------------------------

        /// <summary>Begins bit-level reading at the current byte position.</summary>
        public void StartBitRead()
        {
            _readBitPos = _readPos * 8;
        }

        /// <summary>Ends bit-level reading and moves the byte cursor past the last bit read.</summary>
        public void FinishBitRead()
        {
            _readPos = (_readBitPos + 7) / 8;
            _readBitPos = 0;
        }

        /// <summary>Reads <paramref name="count"/> bits (max 32), most significant first.</summary>
        public int ReadBits(int count)
        {
            if (count <= 0) return 0;
            if (count > 32) throw new ProtocolException("Bit count out of range: " + count);
            if (_readBitPos + count > _writePos * 8) throw new ProtocolException("Bit read past end of packet.");

            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _readBitPos >> 3;
                int bitIndex = 7 - (_readBitPos & 7);
                value = (value << 1) | ((_data[byteIndex] >> bitIndex) & 1);
                _readBitPos++;
            }
            return value;
        }

        // -- reading ----------------------------------------------------------
        private void Require(int count)
        {
            if (_readPos + count > _writePos)
            {
                throw new ProtocolException(string.Format(
                    "Packet underrun: need {0} byte(s) at offset {1}, only {2} available.",
                    count, _readPos, _writePos - _readPos));
            }
        }

        public int ReadUnsignedByte()
        {
            Require(1);
            return _data[_readPos++] & 0xFF;
        }

        public sbyte ReadByte()
        {
            Require(1);
            return (sbyte)_data[_readPos++];
        }

        /// <summary>Reads a byte written by <see cref="WriteByteS"/>.</summary>
        public int ReadUnsignedByteS()
        {
            Require(1);
            return (_data[_readPos++] - 128) & 0xFF;
        }

        /// <summary>Reads a byte written by <see cref="WriteByteC"/>.</summary>
        public int ReadUnsignedByteC()
        {
            Require(1);
            return (-_data[_readPos++]) & 0xFF;
        }

        /// <summary>Reads a byte written by <see cref="WriteByteS"/> as a plain unsigned value.</summary>
        public int ReadUnsignedByteA()
        {
            Require(1);
            return (_data[_readPos++] - 128) & 0xFF;
        }

        public int ReadUnsignedShort()
        {
            Require(2);
            return (_data[_readPos++] << 8) | (_data[_readPos++] & 0xFF);
        }

        public int ReadUnsignedShortLE()
        {
            Require(2);
            return (_data[_readPos++] & 0xFF) | (_data[_readPos++] << 8);
        }

        /// <summary>Reads a 16-bit value where the second byte is offset by 128.</summary>
        public int ReadUnsignedShortA()
        {
            Require(2);
            int hi = _data[_readPos++] & 0xFF;
            int lo = (_data[_readPos++] - 128) & 0xFF;
            return (hi << 8) | lo;
        }

        /// <summary>Reads the form written by <see cref="WriteShortLEA"/>.</summary>
        public int ReadUnsignedShortLEA()
        {
            Require(2);
            int lo = (_data[_readPos++] - 128) & 0xFF;
            int hi = _data[_readPos++] & 0xFF;
            return (hi << 8) | lo;
        }

        public int ReadInt()
        {
            Require(4);
            return (_data[_readPos++] << 24)
                 | ((_data[_readPos++] & 0xFF) << 16)
                 | ((_data[_readPos++] & 0xFF) << 8)
                 | (_data[_readPos++] & 0xFF);
        }

        public int ReadIntLE()
        {
            Require(4);
            return (_data[_readPos++] & 0xFF)
                 | ((_data[_readPos++] & 0xFF) << 8)
                 | ((_data[_readPos++] & 0xFF) << 16)
                 | (_data[_readPos++] << 24);
        }

        /// <summary>Reads the middle-endian form written by <see cref="WriteIntME"/>.</summary>
        public int ReadIntME()
        {
            Require(4);
            int a = _data[_readPos++] & 0xFF;
            int b = _data[_readPos++] & 0xFF;
            int c = _data[_readPos++] & 0xFF;
            int d = _data[_readPos++] & 0xFF;
            return (b << 24) | (a << 16) | (d << 8) | c;
        }

        public long ReadLong()
        {
            Require(8);
            long hi = (uint)ReadInt();
            long lo = (uint)ReadInt();
            return (hi << 32) | lo;
        }

        /// <summary>Reads a 317 "smart" value.</summary>
        public int ReadSmart()
        {
            Require(1);
            int peek = _data[_readPos] & 0xFF;
            if (peek < 128) return ReadUnsignedByte();
            return ReadUnsignedShort() - 32768;
        }

        /// <summary>Reads the interface-id style smart value.</summary>
        public int ReadSmartA()
        {
            Require(1);
            int peek = _data[_readPos] & 0xFF;
            if (peek < 128) return ReadUnsignedByte();
            return ReadUnsignedShort() - 32768;
        }

        /// <summary>Reads a null-terminated string.</summary>
        public string ReadString()
        {
            int start = _readPos;
            while (_readPos < _writePos && _data[_readPos] != 0) _readPos++;
            string value = TextEncoding.GetString(_data, start, _readPos - start);
            if (_readPos < _writePos) _readPos++;    // consume the terminator
            return value;
        }

        /// <summary>
        /// Reads a string terminated by a newline byte (0x0A) instead of a NUL.
        /// </summary>
        /// <remarks>
        /// Cache definition files store the "examine" text this way: it is a free-form
        /// block that may contain commas, quotes and other punctuation, so a newline is
        /// used as the delimiter. Reading it as a NUL-terminated string desynchronises
        /// the whole definition stream, which shows up as garbage characters glued to
        /// the front of the next field.
        /// </remarks>
        public string ReadNewlineString()
        {
            int start = _readPos;
            while (_readPos < _writePos && _data[_readPos] != 10) _readPos++;
            string value = TextEncoding.GetString(_data, start, _readPos - start);
            if (_readPos < _writePos) _readPos++;    // consume the newline
            return value;
        }

        /// <summary>Reads exactly <paramref name="length"/> bytes into a new array.</summary>
        public byte[] ReadBytes(int length)
        {
            Require(length);
            var result = new byte[length];
            Buffer.BlockCopy(_data, _readPos, result, 0, length);
            _readPos += length;
            return result;
        }

        /// <summary>Reads a run of bytes of length <c>count</c>, reversing it.</summary>
        public byte[] ReadBytesReversed(int length)
        {
            byte[] result = ReadBytes(length);
            Array.Reverse(result);
            return result;
        }

        /// <summary>Advances the read cursor, discarding bytes.</summary>
        public void Skip(int count)
        {
            Require(count);
            _readPos += count;
        }

        /// <summary>Copies the payload into a right-sized array.</summary>
        public byte[] ToArray()
        {
            var result = new byte[_writePos];
            Buffer.BlockCopy(_data, 0, result, 0, _writePos);
            return result;
        }

        /// <summary>Hex dump for logs and packet tracing.</summary>
        public string ToHexDump(int maxBytes = 64)
        {
            int count = Math.Min(maxBytes, _writePos);
            var sb = new StringBuilder(count * 3 + 16);
            for (int i = 0; i < count; i++)
            {
                sb.Append(_data[i].ToString("x2"));
                if (i != count - 1) sb.Append(' ');
            }
            if (_writePos > count) sb.Append(" ... (").Append(_writePos).Append(" bytes)");
            return sb.ToString();
        }

        public override string ToString()
        {
            return string.Format("PacketBuffer[{0} bytes, read {1}]", _writePos, _readPos);
        }
    }

    /// <summary>Text encoding shared by the protocol (CP1252, matching the 317 client).</summary>
    public static class TextEncoding
    {
        private static readonly Encoding Encoder;

        static TextEncoding()
        {
            try
            {
                Encoder = Encoding.GetEncoding(1252);
            }
            catch (NotSupportedException)
            {
                // CP1252 should always be present, but never let a missing code page
                // stop the server from booting.
                Encoder = Encoding.ASCII;
            }
        }

        public static byte[] GetBytes(string value)
        {
            return Encoder.GetBytes(value ?? string.Empty);
        }

        public static string GetString(byte[] buffer, int offset, int count)
        {
            return Encoder.GetString(buffer, offset, count);
        }

        /// <summary>
        /// Validates a player name: 1-12 characters, letters, digits, spaces, hyphens
        /// and underscores. Returns <c>null</c> when valid, otherwise the reason.
        /// </summary>
        public static string ValidatePlayerName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Name is empty.";
            if (name.Length > 12) return "Name is longer than 12 characters.";

            bool hasLetter = false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == ' ' || c == '-' || c == '_';
                if (!ok) return "Name contains an invalid character: '" + c + "'.";
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) hasLetter = true;
            }

            if (!hasLetter) return "Name must contain at least one letter.";
            if (name[0] == ' ' || name[name.Length - 1] == ' ') return "Name cannot start or end with a space.";
            if (name.IndexOf("  ", StringComparison.Ordinal) >= 0) return "Name cannot contain double spaces.";

            return null;
        }

        /// <summary>Normalises a name for comparison and storage: lowercase, trimmed.</summary>
        public static string NormaliseName(string name)
        {
            return (name ?? string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>Renders a name for display: first letter of each word capitalised.</summary>
        public static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var sb = new StringBuilder(name.Length);
            bool capitalise = true;
            foreach (char c in name.Trim())
            {
                if (c == ' ')
                {
                    sb.Append(c);
                    capitalise = true;
                }
                else if (capitalise)
                {
                    sb.Append(char.ToUpperInvariant(c));
                    capitalise = false;
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Thrown when a packet is malformed or a peer violates the protocol.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
        public ProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}
