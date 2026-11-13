using System;
using System.Numerics;

namespace DartScape.Core.Crypto
{
    /// <summary>
    /// Bit-level helpers for <see cref="BigInteger"/>.
    /// </summary>
    /// <remarks>
    /// .NET Framework 4.0 predates <c>BigInteger.GetBitLength()</c> and
    /// <c>GetByteCount()</c>, which only arrived with .NET Core. These are the
    /// equivalents, written so the rest of the code reads the same as it would on a
    /// modern runtime.
    /// </remarks>
    public static class BigIntUtil
    {
        /// <summary>Number of significant bits in the magnitude of <paramref name="value"/>.</summary>
        public static int GetBitLength(BigInteger value)
        {
            if (value.Sign == 0) return 0;

            byte[] bytes = value.ToByteArray();   // little-endian, two's complement
            int last = bytes.Length - 1;

            // A trailing 0x00 (or 0xFF for negatives) is a pure sign byte.
            if (value.Sign > 0 && last > 0 && bytes[last] == 0) last--;
            if (value.Sign < 0 && last > 0 && bytes[last] == 0xFF) last--;

            if (last < 0) return 0;

            byte top = bytes[last];
            int bits = last * 8;
            while (top != 0)
            {
                bits++;
                top >>= 1;
            }
            return bits;
        }

        /// <summary>Minimum number of bytes needed to hold the magnitude of <paramref name="value"/>.</summary>
        public static int GetByteCount(BigInteger value)
        {
            if (value.Sign == 0) return 1;
            int bits = GetBitLength(value);
            return (bits + 7) / 8;
        }

        /// <summary>True when the value is odd (the only interesting parity test here).</summary>
        public static bool IsOdd(BigInteger value)
        {
            return !value.IsEven;
        }

        /// <summary>
        /// Reads an unsigned big-endian integer of arbitrary length.
        /// </summary>
        public static BigInteger FromBigEndian(byte[] buffer, int offset, int length)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || length < 0 || offset + length > buffer.Length) throw new ArgumentOutOfRangeException("length");

            var little = new byte[length + 1];
            for (int i = 0; i < length; i++)
            {
                little[i] = buffer[offset + length - 1 - i];
            }
            return new BigInteger(little);
        }
    }
}
