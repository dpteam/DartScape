using System;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// The colour palette the original client builds at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not decoration. RuneScape's terrain and avatar colours are not stored as RGB
    /// anywhere: they are stored as <em>packed HSL</em> indices into a 65536-entry table that
    /// the client generates from a hue/saturation/lightness triple with a gamma curve applied.
    /// Feeding the raw numbers through as RGB is what makes a hand-rolled client look wrong in
    /// a way that is hard to name — the ground goes muddy and flat, and skin tones come out
    /// either grey or, if a table entry is white, like a ghost.
    /// </para>
    /// <para>
    /// The index layout is <c>(hue &lt;&lt; 10) | (saturation &lt;&lt; 7) | lightness</c>, with
    /// hue in 0..63, saturation in 0..7 and lightness in 0..127. That is exactly the packing
    /// the original's <c>method177</c> produces, and the table is generated in the same order so
    /// the two agree without a translation step.
    /// </para>
    /// <para>
    /// The gamma of 0.8 is the value the original uses at full detail. It is doing a lot of
    /// work: without it every midtone is about a third too dark, which reads as "the world is
    /// in shadow" rather than as a colour problem.
    /// </para>
    /// </remarks>
    public static class Palette
    {
        /// <summary>Hue steps in the table.</summary>
        private const int HueSteps = 64;

        /// <summary>Saturation steps in the table.</summary>
        private const int SaturationSteps = 8;

        /// <summary>Lightness steps in the table.</summary>
        private const int LightnessSteps = 128;

        /// <summary>Gamma the original applies at full graphics detail.</summary>
        private const double Gamma = 0.8;

        private static readonly int[] Table = new int[HueSteps * SaturationSteps * LightnessSteps];
        private static readonly object Sync = new object();
        private static bool _built;

        /// <summary>
        /// The appearance colours, packed HSL, one row per body part.
        /// </summary>
        /// <remarks>
        /// Hair, torso, legs, feet, skin — in the order the appearance packet sends them. These
        /// are the original's values, transcribed. The first entry of each row is also the
        /// colour the part is defined with, which is why an avatar that selects index 0 looks
        /// unmodified.
        /// </remarks>
        private static readonly int[][] Appearance =
        {
            new[] { 6798, 107, 10283, 16, 4797, 7744, 5799, 4634, 33697, 22433, 2983, 54193 },
            new[] { 8741, 12, 64030, 43162, 7735, 8404, 1701, 38430, 24094, 10153, 56621, 4783, 1341, 16578, 35003, 25239 },
            new[] { 25238, 8742, 12, 64030, 43162, 7735, 8404, 1701, 38430, 24094, 10153, 56621, 4783, 1341, 16578, 35003 },
            new[] { 4626, 11146, 6439, 12, 4758, 10270 },
            new[] { 4550, 4537, 5681, 5673, 5790, 6806, 8076, 4574 }
        };

        /// <summary>Body parts, in appearance-packet order.</summary>
        public const int Hair = 0;
        public const int Torso = 1;
        public const int Legs = 2;
        public const int Feet = 3;
        public const int Skin = 4;

        /// <summary>
        /// Colour of one body part.
        /// </summary>
        /// <param name="part">0 hair, 1 torso, 2 legs, 3 feet, 4 skin.</param>
        /// <param name="index">The value from the appearance packet; wrapped into range.</param>
        /// <remarks>
        /// Wrapping rather than clamping is deliberate: a value out of range is a client/server
        /// disagreement, and wrapping keeps the avatar visible instead of turning it black.
        /// </remarks>
        public static int Part(int part, int index)
        {
            if (part < 0) part = 0;
            if (part >= Appearance.Length) part = Appearance.Length - 1;

            int[] row = Appearance[part];
            int i = index % row.Length;
            if (i < 0) i += row.Length;

            return FromPacked(row[i]);
        }

        /// <summary>Converts a packed HSL index to RGB.</summary>
        /// <remarks>
        /// The result is opaque ARGB, not bare <c>0xRRGGBB</c>. The framebuffer stores pixels as
        /// ARGB and writes them through unchanged, so a colour with a zero alpha byte lands in
        /// the buffer as a fully transparent pixel — which renders as a hole in the window and
        /// as flat white in a saved PNG. That is a spectacularly confusing way to find out that
        /// a byte is missing.
        /// </remarks>
        public static int FromPacked(int packed)
        {
            EnsureBuilt();

            // Only the low 17 bits are meaningful: 6 bits of hue, 3 of saturation, 7 of
            // lightness. Masking here means a caller that hands over a stale value from an
            // older palette format still gets a colour rather than an exception.
            int index = packed & 0x1FFFF;

            if (index >= Table.Length) index = Table.Length - 1;

            return unchecked((int)0xFF000000) | Table[index];
        }

        /// <summary>
        /// Packs hue, saturation and lightness into a table index.
        /// </summary>
        /// <param name="hue">0..255, as the cache stores it.</param>
        /// <param name="saturation">0..255.</param>
        /// <param name="lightness">0..255.</param>
        public static int Pack(int hue, int saturation, int lightness)
        {
            if (hue < 0) hue = 0; else if (hue > 255) hue = 255;
            if (saturation < 0) saturation = 0; else if (saturation > 255) saturation = 255;
            if (lightness < 0) lightness = 0; else if (lightness > 255) lightness = 255;

            return (hue / 4 << 10) + (saturation / 32 << 7) + lightness / 2;
        }

        /// <summary>
        /// Scales the lightness component of a packed index.
        /// </summary>
        /// <param name="packed">A packed HSL index.</param>
        /// <param name="scale">Target lightness out of 128; the original uses 96 for terrain.</param>
        /// <remarks>
        /// Terrain is drawn at three-quarters lightness, which is what stops the ground
        /// competing with the objects standing on it.
        /// </remarks>
        public static int ScaleLightness(int packed, int scale)
        {
            int light = scale * (packed & 0x7F) / 128;

            if (light < 2) light = 2;
            else if (light > 126) light = 126;

            return (packed & 0xFF80) + light;
        }

        /// <summary>
        /// Converts a packed <c>0xRRGGBB</c> colour to a packed HSL index.
        /// </summary>
        /// <remarks>
        /// The same conversion the original applies to every floor definition at load time.
        /// Rounding differs slightly from the reference (which truncates) but the result lands
        /// in the same palette cell for every colour that matters.
        /// </remarks>
        public static int FromRgb(int rgb)
        {
            double r = (rgb >> 16 & 0xFF) / 256.0;
            double g = (rgb >> 8 & 0xFF) / 256.0;
            double b = (rgb & 0xFF) / 256.0;

            double min = r;
            if (g < min) min = g;
            if (b < min) min = b;

            double max = r;
            if (g > max) max = g;
            if (b > max) max = b;

            double hue = 0.0;
            double saturation = 0.0;
            double lightness = (min + max) / 2.0;

            if (min != max)
            {
                if (lightness < 0.5) saturation = (max - min) / (max + min);
                else saturation = (max - min) / (2.0 - max - min);

                if (r == max) hue = (g - b) / (max - min);
                else if (g == max) hue = 2.0 + (b - r) / (max - min);
                else hue = 4.0 + (r - g) / (max - min);
            }

            hue /= 6.0;

            int h = (int)(hue * 256.0);
            int s = (int)(saturation * 256.0);
            int l = (int)(lightness * 256.0);

            if (s < 0) s = 0; else if (s > 255) s = 255;
            if (l < 0) l = 0; else if (l > 255) l = 255;

            return Pack(h, s, l);
        }

        /// <summary>
        /// Converts a packed <c>0xRRGGBB</c> colour straight to the shaded RGB the world uses.
        /// </summary>
        /// <remarks>
        /// Terrain is drawn through this rather than through <see cref="FromRgb"/> followed by
        /// <see cref="FromPacked"/> by the caller, because the lightness scaling has to happen
        /// between the two.
        /// </remarks>
        public static int TerrainColour(int rgb)
        {
            return FromPacked(ScaleLightness(FromRgb(rgb), 96));
        }

        /// <summary>
        /// Converts a packed <c>0xRRGGBB</c> colour to the shaded RGB the world uses, keeping
        /// the alpha channel.
        /// </summary>
        /// <remarks>
        /// <see cref="FromPacked"/> already returns opaque ARGB, so this is only here to make
        /// the intent obvious at the call site.
        /// </remarks>
        public static int Shaded(int rgb)
        {
            return TerrainColour(rgb);
        }

        /// <summary>Builds the table on first use.</summary>
        private static void EnsureBuilt()
        {
            if (_built) return;

            lock (Sync)
            {
                if (_built) return;

                for (int hueStep = 0; hueStep < HueSteps; hueStep++)
                {
                    for (int saturationStep = 0; saturationStep < SaturationSteps; saturationStep++)
                    {
                        double hue = (double)hueStep / HueSteps;
                        double saturation = (double)saturationStep / SaturationSteps + 0.0625;

                        for (int lightnessStep = 0; lightnessStep < LightnessSteps; lightnessStep++)
                        {
                            double lightness = (double)lightnessStep / LightnessSteps;

                            int index = ((hueStep * SaturationSteps + saturationStep) * LightnessSteps)
                                      + lightnessStep;

                            Table[index] = Hsl(hue, saturation, lightness);
                        }
                    }
                }

                _built = true;
            }
        }

        /// <summary>One HSL triple to a packed <c>0xRRGGBB</c>, with the gamma applied.</summary>
        private static int Hsl(double hue, double saturation, double lightness)
        {
            double r = lightness;
            double g = lightness;
            double b = lightness;

            if (saturation != 0.0)
            {
                double second = lightness < 0.5
                    ? lightness * (1.0 + saturation)
                    : lightness + saturation - lightness * saturation;

                double first = 2.0 * lightness - second;

                r = Channel(first, second, hue + 1.0 / 3.0);
                g = Channel(first, second, hue);
                b = Channel(first, second, hue - 1.0 / 3.0);
            }

            int ir = Gamma8(r);
            int ig = Gamma8(g);
            int ib = Gamma8(b);

            // The original never allows a black entry: it substitutes 1 so a lookup can never
            // be mistaken for "no colour".
            int rgb = (ir << 16) | (ig << 8) | ib;
            return rgb == 0 ? 1 : rgb;
        }

        private static double Channel(double first, double second, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;

            if (6.0 * t < 1.0) return first + (second - first) * 6.0 * t;
            if (2.0 * t < 1.0) return second;
            if (3.0 * t < 2.0) return first + (second - first) * (2.0 / 3.0 - t) * 6.0;
            return first;
        }

        /// <summary>Applies the palette gamma to one channel in 0..1.</summary>
        private static int Gamma8(double channel)
        {
            if (channel <= 0.0) return 0;

            double value = Math.Pow(channel, Gamma) * 256.0;

            int result = (int)value;
            if (result > 255) result = 255;
            if (result < 0) result = 0;

            return result;
        }
    }
}
