using System;

namespace DartScape.Core.Crypto
{
    /// <summary>
    /// ISAAC (Indirection, Shift, Accumulate, Add and Count) CSPRNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 317 protocol obfuscates the packet opcode stream with ISAAC seeded from a
    /// 32-bit-int array exchanged during login. Both ends must produce identical
    /// keystreams, so this is a bit-exact reimplementation of Bob Jenkins' reference
    /// algorithm — do not "improve" it.
    /// </para>
    /// <para>
    /// It is not used here as a security primitive (it isn't one, by modern standards);
    /// it is a wire format detail.
    /// </para>
    /// </remarks>
    public sealed class IsaacRandom
    {
        private const int Size = 256;
        private const int Mask = Size - 1;
        private const int GoldenRatio = unchecked((int)0x9E3779B9);

        private readonly int[] _results = new int[Size];
        private readonly int[] _memory = new int[Size];
        private int _a;
        private int _b;
        private int _c;
        private int _count;

        /// <summary>Creates a generator seeded from <paramref name="seed"/> (32 ints).</summary>
        public IsaacRandom(int[] seed)
        {
            if (seed == null) throw new ArgumentNullException("seed");

            for (int i = 0; i < Size; i++) _results[i] = 0;

            // Mix the seed into the state, repeating it until all slots are filled.
            var mixed = new int[Size];
            for (int i = 0; i < seed.Length && i < Size; i++) mixed[i] = seed[i];

            int[] state = new int[Size * 2 + 4];
            for (int i = 0; i < Size; i++)
            {
                state[i] = GoldenRatio;
            }
            for (int i = 0; i < Size; i++)
            {
                state[i] += mixed[i & (seed.Length - 1)];
            }

            // Two passes of the mixing function, exactly as in the reference.
            int aa = 0, bb = 0, cc = 0, dd = 0, ee = 0, ff = 0, gg = 0, hh = 0;

            for (int i = 0; i < 4; i++)
            {
                aa ^= bb << 11; dd += aa; bb += cc;
                bb ^= cc >> 2;  ee += bb; cc += dd;
                cc ^= dd << 8;  ff += cc; dd += ee;
                dd ^= ee >> 16; gg += dd; ee += ff;
                ee ^= ff << 10; hh += ee; ff += gg;
                ff ^= gg >> 4;  aa += ff; gg += hh;
                gg ^= hh << 8;  bb += gg; hh += aa;
                hh ^= aa >> 9;  cc += hh; aa += bb;
            }

            for (int i = 0; i < Size; i += 8)
            {
                aa += state[i];     bb += state[i + 1];
                cc += state[i + 2]; dd += state[i + 3];
                ee += state[i + 4]; ff += state[i + 5];
                gg += state[i + 6]; hh += state[i + 7];

                aa ^= bb << 11; dd += aa; bb += cc;
                bb ^= cc >> 2;  ee += bb; cc += dd;
                cc ^= dd << 8;  ff += cc; dd += ee;
                dd ^= ee >> 16; gg += dd; ee += ff;
                ee ^= ff << 10; hh += ee; ff += gg;
                ff ^= gg >> 4;  aa += ff; gg += hh;
                gg ^= hh << 8;  bb += gg; hh += aa;
                hh ^= aa >> 9;  cc += hh; aa += bb;

                _memory[i] = aa;     _memory[i + 1] = bb;
                _memory[i + 2] = cc; _memory[i + 3] = dd;
                _memory[i + 4] = ee; _memory[i + 5] = ff;
                _memory[i + 6] = gg; _memory[i + 7] = hh;
            }

            for (int i = 0; i < Size; i += 8)
            {
                aa += _memory[i];     bb += _memory[i + 1];
                cc += _memory[i + 2]; dd += _memory[i + 3];
                ee += _memory[i + 4]; ff += _memory[i + 5];
                gg += _memory[i + 6]; hh += _memory[i + 7];

                aa ^= bb << 11; dd += aa; bb += cc;
                bb ^= cc >> 2;  ee += bb; cc += dd;
                cc ^= dd << 8;  ff += cc; dd += ee;
                dd ^= ee >> 16; gg += dd; ee += ff;
                ee ^= ff << 10; hh += ee; ff += gg;
                ff ^= gg >> 4;  aa += ff; gg += hh;
                gg ^= hh << 8;  bb += gg; hh += aa;
                hh ^= aa >> 9;  cc += hh; aa += bb;

                _memory[i] = aa;     _memory[i + 1] = bb;
                _memory[i + 2] = cc; _memory[i + 3] = dd;
                _memory[i + 4] = ee; _memory[i + 5] = ff;
                _memory[i + 6] = gg; _memory[i + 7] = hh;
            }

            Isacc();

            _count = Size;
        }

        /// <summary>Returns the next 32-bit value from the keystream.</summary>
        public int NextInt()
        {
            if (_count-- == 0)
            {
                Isacc();
                _count = Size - 1;
            }
            return _results[_count];
        }

        /// <summary>
        /// The 317 protocol consumes exactly one value per opcode, but the client reads
        /// it as an unsigned byte pair. This helper keeps that intent obvious at call
        /// sites: <c>var key = (isaac.NextInt() + opcode) &amp; 0xFF;</c>.
        /// </summary>
        public int NextIntLowByte()
        {
            return NextInt() & 0xFF;
        }

        private void Isacc()
        {
            unchecked
            {
                _c++;
                _b += _c;

                for (int i = 0; i < Size; i++)
                {
                    int x = _memory[i];

                    switch (i & 3)
                    {
                        case 0: _a ^= _a << 13; break;
                        case 1: _a ^= (int)((uint)_a >> 6); break;
                        case 2: _a ^= _a << 2; break;
                        case 3: _a ^= (int)((uint)_a >> 16); break;
                    }

                    _a += _memory[(i + 128) & Mask];
                    int y = _memory[(x >> 2) & Mask] + _a + _b;
                    _memory[i] = y;
                    _b = _memory[(y >> 10) & Mask] + x;
                    _results[i] = _b;
                }
            }
        }

        /// <summary>
        /// Builds the 4-int seed the 317 login block carries, derived from the
        /// client's random session seed. Kept here so client and server cannot drift.
        /// </summary>
        public static int[] BuildSeed(long sessionSeed)
        {
            unchecked
            {
                var seed = new int[4];
                seed[0] = (int)(sessionSeed >> 32);
                seed[1] = (int)sessionSeed + 1;
                seed[2] = (int)(sessionSeed >> 32) + 2;
                seed[3] = (int)sessionSeed + 3;
                return seed;
            }
        }
    }
}
