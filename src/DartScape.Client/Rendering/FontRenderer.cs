using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// Text drawing with a cache of pre-rendered strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The renderer has no bitmap font of its own, and hand-authoring one for a Unicode
    /// client would be a poor use of the bytes. Instead each distinct
    /// (text, colour, size, bold) combination is rendered once by GDI+ into a small
    /// <see cref="Bitmap"/> and then blitted like any other sprite.
    /// </para>
    /// <para>
    /// The cache is the important part. Chat, names and interface labels repeat
    /// constantly, so after the first frame almost every draw is a lookup. Entries are
    /// evicted oldest-first when the cache grows past a bound, which keeps memory flat
    /// even after a long session of varied chat.
    /// </para>
    /// </remarks>
    public sealed class FontRenderer : IDisposable
    {
        private const int MaxEntries = 900;

        private sealed class Entry
        {
            public Bitmap Bitmap;
            public int Width;
            public int Height;
            public long LastUsed;
        }

        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _evictionOrder = new List<string>();
        private long _useCounter;

        private readonly Font _fontSmall;
        private readonly Font _fontNormal;
        private readonly Font _fontBold;
        private readonly Font _fontTitle;
        private readonly StringFormat _format;

        public FontRenderer()
        {
            // A system font stack, so the client looks right on XP and modern Windows alike.
            _fontSmall = new Font("Tahoma", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _fontNormal = new Font("Tahoma", 10f, FontStyle.Regular, GraphicsUnit.Point);
            _fontBold = new Font("Tahoma", 10f, FontStyle.Bold, GraphicsUnit.Point);
            _fontTitle = new Font("Georgia", 26f, FontStyle.Bold, GraphicsUnit.Point);

            _format = (StringFormat)StringFormat.GenericTypographic.Clone();
            _format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            _format.Trimming = StringTrimming.None;
        }

        /// <summary>Text sizes the client uses.</summary>
        public enum Size
        {
            Small = 0,
            Normal = 1,
            Bold = 2,
            Title = 3
        }

        private Font FontFor(Size size)
        {
            switch (size)
            {
                case Size.Small: return _fontSmall;
                case Size.Bold: return _fontBold;
                case Size.Title: return _fontTitle;
                default: return _fontNormal;
            }
        }

        /// <summary>Measures a string without rendering it.</summary>
        public SizeF Measure(string text, Size size)
        {
            if (string.IsNullOrEmpty(text)) return new SizeF(0, FontFor(size).Height);

            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                return graphics.MeasureString(text, FontFor(size), int.MaxValue, _format);
            }
        }

        /// <summary>Draws a string with the top-left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public void Draw(Framebuffer target, string text, int x, int y, int argb, Size size = Size.Normal, bool shadow = true)
        {
            if (string.IsNullOrEmpty(text) || target == null) return;

            if (shadow)
            {
                Entry shadowEntry = GetEntry(text, unchecked((int)0xFF000000), size);
                if (shadowEntry != null) target.DrawBitmap(shadowEntry.Bitmap, x + 1, y + 1);
            }

            Entry entry = GetEntry(text, argb, size);
            if (entry != null) target.DrawBitmap(entry.Bitmap, x, y);
        }

        /// <summary>Draws a string centred horizontally inside <paramref name="width"/>.</summary>
        public void DrawCentred(Framebuffer target, string text, int x, int y, int width, int argb,
            Size size = Size.Normal, bool shadow = true)
        {
            Entry entry = GetEntry(text, argb, size);
            if (entry == null) return;

            int offset = (width - entry.Width) / 2;
            Draw(target, text, x + offset, y, argb, size, shadow);
        }

        /// <summary>Draws a string right-aligned to <paramref name="right"/>.</summary>
        public void DrawRight(Framebuffer target, string text, int right, int y, int argb,
            Size size = Size.Normal, bool shadow = true)
        {
            Entry entry = GetEntry(text, argb, size);
            if (entry == null) return;

            Draw(target, text, right - entry.Width, y, argb, size, shadow);
        }

        /// <summary>Wraps text to <paramref name="maxWidth"/> and returns the lines.</summary>
        public List<string> Wrap(string text, int maxWidth, Size size = Size.Normal)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            string[] words = text.Split(' ');
            string current = string.Empty;

            foreach (string word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (Measure(candidate, size).Width > maxWidth && current.Length > 0)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            if (current.Length > 0) lines.Add(current);
            return lines;
        }

        private Entry GetEntry(string text, int argb, Size size)
        {
            string key = ((int)size).ToString() + ":" + argb.ToString("X8") + ":" + text;

            Entry entry;
            if (_cache.TryGetValue(key, out entry))
            {
                entry.LastUsed = ++_useCounter;
                return entry;
            }

            SizeF measured = Measure(text, size);
            int width = Math.Max(1, (int)Math.Ceiling(measured.Width) + 2);
            int height = Math.Max(1, (int)Math.Ceiling(measured.Height) + 2);

            // Refuse absurd sizes rather than allocating a huge bitmap for a long line.
            if (width > 2048 || height > 256) return null;

            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);

                int a = (argb >> 24) & 0xFF;
                int r = (argb >> 16) & 0xFF;
                int g = (argb >> 8) & 0xFF;
                int b = argb & 0xFF;

                using (var brush = new SolidBrush(Color.FromArgb(a, r, g, b)))
                {
                    graphics.DrawString(text, FontFor(size), brush, 0, 0, _format);
                }
            }

            entry = new Entry { Bitmap = bitmap, Width = width, Height = height, LastUsed = ++_useCounter };
            _cache[key] = entry;
            _evictionOrder.Add(key);

            if (_cache.Count > MaxEntries) Evict();

            return entry;
        }

        private void Evict()
        {
            // Drop the least recently used quarter. Cheap, and the working set of visible
            // strings is far smaller than the bound, so this almost never runs.
            int toRemove = Math.Max(1, _cache.Count / 4);

            var byAge = new List<KeyValuePair<string, Entry>>(_cache);
            byAge.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));

            for (int i = 0; i < toRemove && i < byAge.Count; i++)
            {
                string key = byAge[i].Key;
                Entry entry = byAge[i].Value;
                try { entry.Bitmap.Dispose(); } catch (Exception) { }
                _cache.Remove(key);
            }

            _evictionOrder.Clear();
            _evictionOrder.AddRange(_cache.Keys);
        }

        /// <summary>Drops every cached string. Called when the theme changes.</summary>
        public void Invalidate()
        {
            foreach (KeyValuePair<string, Entry> pair in _cache)
            {
                try { pair.Value.Bitmap.Dispose(); } catch (Exception) { }
            }
            _cache.Clear();
            _evictionOrder.Clear();
        }

        public void Dispose()
        {
            Invalidate();
            _format.Dispose();
            _fontSmall.Dispose();
            _fontNormal.Dispose();
            _fontBold.Dispose();
            _fontTitle.Dispose();
        }
    }
}
