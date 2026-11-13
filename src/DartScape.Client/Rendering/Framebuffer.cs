using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DartScape.Client.Rendering
{
    /// <summary>
    /// A 32-bit ARGB software framebuffer with the handful of primitives the client needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is drawn into a plain <c>int[]</c> and then blitted to the window in one
    /// go. That is the same model the original client used, and it is the right one here:
    /// it works on any Windows XP driver, it has no dependency on OpenGL or Direct3D, and
    /// it makes the renderer trivially testable (compare two buffers).
    /// </para>
    /// <para>
    /// Pixels are stored as <c>0xAARRGGBB</c> in a managed array. The blit converts to the
    /// GDI+ layout once per frame with a single <see cref="Bitmap.LockBits"/> copy, which
    /// measured far faster than per-pixel <c>SetPixel</c> calls or drawing into a
    /// <c>Graphics</c> per primitive.
    /// </para>
    /// </remarks>
    public sealed class Framebuffer : IDisposable
    {
        private readonly int[] _pixels;
        private readonly Bitmap _bitmap;
        private readonly BitmapData _data;
        private readonly IntPtr _scan0;
        private bool _disposed;

        public Framebuffer(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");

            Width = width;
            Height = height;
            _pixels = new int[width * height];

            _bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            _data = _bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            _scan0 = _data.Scan0;

            ResetClip();
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        // Clip rectangle, inclusive-exclusive. Everything respects it, so the world
        // renderer can draw a scene into its own viewport without spilling into the
        // chat box or the sidebar.
        private int _clipX;
        private int _clipY;
        private int _clipRight;
        private int _clipBottom;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        private static extern unsafe void CopyMemory(void* destination, void* source, long length);

        /// <summary>Restricts drawing to a rectangle. Values are clamped to the buffer.</summary>
        public void SetClip(int x, int y, int width, int height)
        {
            _clipX = Math.Max(0, x);
            _clipY = Math.Max(0, y);
            _clipRight = Math.Min(Width, x + width);
            _clipBottom = Math.Min(Height, y + height);
        }

        /// <summary>Removes the clip, allowing drawing over the whole buffer.</summary>
        public void ResetClip()
        {
            _clipX = 0;
            _clipY = 0;
            _clipRight = Width;
            _clipBottom = Height;
        }

        /// <summary>Fills one horizontal run, honouring the clip.</summary>
        public void FillSpan(int x0, int x1, int y, int argb)
        {
            if (y < _clipY || y >= _clipBottom) return;

            int from = Math.Max(x0, _clipX);
            int to = Math.Min(x1, _clipRight);
            if (to <= from) return;

            int row = y * Width;
            for (int x = from; x < to; x++) _pixels[row + x] = argb;
        }

        /// <summary>
        /// Fills a diamond, the shape of one isometric tile.
        /// </summary>
        /// <remarks>
        /// Scanline spans rather than per-pixel tests: a tile is a solid convex shape, so
        /// each row is one contiguous run. At roughly 700 tiles per frame this is the
        /// difference between 50 fps and a slideshow.
        /// </remarks>
        public void FillDiamond(int cx, int cy, int halfWidth, int halfHeight, int argb)
        {
            if (halfWidth <= 0 || halfHeight <= 0) return;

            for (int dy = -halfHeight; dy <= halfHeight; dy++)
            {
                int y = cy + dy;
                if (y < _clipY || y >= _clipBottom) continue;

                // Width shrinks linearly towards the top and bottom points.
                int span = (int)((long)halfWidth * (halfHeight - Math.Abs(dy)) / halfHeight);
                if (span <= 0) continue;

                FillSpan(cx - span, cx + span, y, argb);
            }
        }

        /// <summary>Fills a convex quad given four corner points.</summary>
        public void FillQuad(int x0, int y0, int x1, int y1, int x2, int y2, int x3, int y3, int argb)
        {
            int top = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            int bottom = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));

            if (top < _clipY) top = _clipY;
            if (bottom >= _clipBottom) bottom = _clipBottom - 1;

            for (int y = top; y <= bottom; y++)
            {
                int left = int.MaxValue;
                int right = int.MinValue;

                ScanEdge(x0, y0, x1, y1, y, ref left, ref right);
                ScanEdge(x1, y1, x2, y2, y, ref left, ref right);
                ScanEdge(x2, y2, x3, y3, y, ref left, ref right);
                ScanEdge(x3, y3, x0, y0, y, ref left, ref right);

                if (left > right) continue;
                FillSpan(left, right, y, argb);
            }
        }

        /// <summary>
        /// Fills a triangle with flat shading.
        /// </summary>
        /// <remarks>
        /// A terrain tile whose four corners are at different elevations is not planar, so
        /// it is split into two triangles. Each gets its own shade derived from its own
        /// slope, which is what produces the faceted, lit-from-one-side look of a heightfield
        /// rather than a smooth gradient that reads as a decal.
        /// </remarks>
        public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, int argb)
        {
            int top = Math.Min(y0, Math.Min(y1, y2));
            int bottom = Math.Max(y0, Math.Max(y1, y2));

            if (top < _clipY) top = _clipY;
            if (bottom >= _clipBottom) bottom = _clipBottom - 1;

            for (int y = top; y <= bottom; y++)
            {
                int left = int.MaxValue;
                int right = int.MinValue;

                ScanEdge(x0, y0, x1, y1, y, ref left, ref right);
                ScanEdge(x1, y1, x2, y2, y, ref left, ref right);
                ScanEdge(x2, y2, x0, y0, y, ref left, ref right);

                if (left > right) continue;
                FillSpan(left, right, y, argb);
            }
        }

        /// <summary>Intersects one quad edge with a scanline and widens the span.</summary>
        private static void ScanEdge(int ax, int ay, int bx, int by, int y, ref int left, ref int right)
        {
            if (ay == by)
            {
                // Horizontal edge: it contributes its whole extent on this row.
                if (y != ay) return;
                if (ax < left) left = ax;
                if (bx < left) left = bx;
                if (ax > right) right = ax;
                if (bx > right) right = bx;
                return;
            }

            int minY = Math.Min(ay, by);
            int maxY = Math.Max(ay, by);
            if (y < minY || y > maxY) return;

            // Integer interpolation is exact enough here and avoids a float per pixel.
            int x = ax + (int)((long)(bx - ax) * (y - ay) / (by - ay));

            if (x < left) left = x;
            if (x > right) right = x;
        }

        /// <summary>The raw pixel array. Direct access for the hot paths.</summary>
        public int[] Pixels
        {
            get { return _pixels; }
        }

        /// <summary>Fills the whole buffer.</summary>
        public void Clear(int argb)
        {
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = argb;
        }

        /// <summary>Fills a rectangle, clipped to the buffer and the current clip.</summary>
        public void FillRect(int x, int y, int width, int height, int argb)
        {
            int x0 = Math.Max(_clipX, x);
            int y0 = Math.Max(_clipY, y);
            int x1 = Math.Min(_clipRight, x + width);
            int y1 = Math.Min(_clipBottom, y + height);
            if (x1 <= x0 || y1 <= y0) return;

            for (int py = y0; py < y1; py++)
            {
                int row = py * Width;
                for (int px = x0; px < x1; px++) _pixels[row + px] = argb;
            }
        }

        /// <summary>Draws a rectangle outline.</summary>
        public void DrawRect(int x, int y, int width, int height, int argb)
        {
            FillRect(x, y, width, 1, argb);
            FillRect(x, y + height - 1, width, 1, argb);
            FillRect(x, y, 1, height, argb);
            FillRect(x + width - 1, y, 1, height, argb);
        }

        /// <summary>Draws a filled circle, used for minimap dots and shadows.</summary>
        public void FillCircle(int cx, int cy, int radius, int argb)
        {
            if (radius <= 0) return;

            int r2 = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = cy + dy;
                if (y < _clipY || y >= _clipBottom) continue;

                int span = (int)Math.Sqrt(r2 - dy * dy);
                int x0 = Math.Max(_clipX, cx - span);
                int x1 = Math.Min(_clipRight - 1, cx + span);
                if (x1 < x0) continue;

                int row = y * Width;
                for (int x = x0; x <= x1; x++) _pixels[row + x] = argb;
            }
        }

        /// <summary>
        /// Blends a colour over a pixel, honouring the alpha byte. Used for overlays so
        /// panels can be translucent without a separate compositing pass.
        /// </summary>
        public void BlendPixel(int x, int y, int argb)
        {
            if (x < _clipX || y < _clipY || x >= _clipRight || y >= _clipBottom) return;

            int alpha = (argb >> 24) & 0xFF;
            if (alpha == 255) { _pixels[y * Width + x] = argb; return; }
            if (alpha == 0) return;

            int index = y * Width + x;
            int dst = _pixels[index];

            int inverse = 255 - alpha;
            int r = (((argb >> 16) & 0xFF) * alpha + ((dst >> 16) & 0xFF) * inverse) / 255;
            int g = (((argb >> 8) & 0xFF) * alpha + ((dst >> 8) & 0xFF) * inverse) / 255;
            int b = ((argb & 0xFF) * alpha + (dst & 0xFF) * inverse) / 255;

            _pixels[index] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        /// <summary>Blends a translucent rectangle.</summary>
        public void BlendRect(int x, int y, int width, int height, int argb)
        {
            int alpha = (argb >> 24) & 0xFF;
            if (alpha == 0) return;
            if (alpha == 255) { FillRect(x, y, width, height, argb); return; }

            int x0 = Math.Max(_clipX, x);
            int y0 = Math.Max(_clipY, y);
            int x1 = Math.Min(_clipRight, x + width);
            int y1 = Math.Min(_clipBottom, y + height);

            for (int py = y0; py < y1; py++)
            {
                int row = py * Width;
                for (int px = x0; px < x1; px++)
                {
                    int dst = _pixels[row + px];
                    int inverse = 255 - alpha;
                    int r = (((argb >> 16) & 0xFF) * alpha + ((dst >> 16) & 0xFF) * inverse) / 255;
                    int g = (((argb >> 8) & 0xFF) * alpha + ((dst >> 8) & 0xFF) * inverse) / 255;
                    int b = ((argb & 0xFF) * alpha + (dst & 0xFF) * inverse) / 255;
                    _pixels[row + px] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                }
            }
        }

        /// <summary>Draws a vertical gradient, used for the sky and panel headers.</summary>
        public void VerticalGradient(int x, int y, int width, int height, int topArgb, int bottomArgb)
        {
            if (height <= 0) return;

            for (int i = 0; i < height; i++)
            {
                int py = y + i;
                if (py < 0 || py >= Height) continue;

                double t = (double)i / height;
                int argb = Lerp(topArgb, bottomArgb, t);
                FillRect(x, py, width, 1, argb);
            }
        }

        /// <summary>Linear interpolation between two ARGB colours.</summary>
        public static int Lerp(int from, int to, double t)
        {
            if (t <= 0) return from;
            if (t >= 1) return to;

            int a = (int)(((from >> 24) & 0xFF) + (((to >> 24) & 0xFF) - ((from >> 24) & 0xFF)) * t);
            int r = (int)(((from >> 16) & 0xFF) + (((to >> 16) & 0xFF) - ((from >> 16) & 0xFF)) * t);
            int g = (int)(((from >> 8) & 0xFF) + (((to >> 8) & 0xFF) - ((from >> 8) & 0xFF)) * t);
            int b = (int)((from & 0xFF) + ((to & 0xFF) - (from & 0xFF)) * t);

            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>Packs RGB into the framebuffer's ARGB layout.</summary>
        public static int Rgb(int r, int g, int b)
        {
            return unchecked((int)0xFF000000) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
        }

        /// <summary>Packs ARGB.</summary>
        public static int Argb(int a, int r, int g, int b)
        {
            return ((a & 0xFF) << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
        }

        /// <summary>Draws a cached GDI+ bitmap, used for the title screen and sprites.</summary>
        public void DrawBitmap(Bitmap source, int x, int y)
        {
            if (source == null) return;

            int w = source.Width;
            int h = source.Height;

            BitmapData data = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[w];
                for (int py = 0; py < h; py++)
                {
                    int dy = y + py;
                    if (dy < 0 || dy >= Height) continue;

                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)py * data.Stride), row, 0, w);
                    int destRow = dy * Width;

                    for (int px = 0; px < w; px++)
                    {
                        int dx = x + px;
                        if (dx < 0 || dx >= Width) continue;

                        int src = row[px];
                        int alpha = (src >> 24) & 0xFF;
                        if (alpha == 0) continue;
                        if (alpha == 255) { _pixels[destRow + dx] = src; continue; }

                        int dst = _pixels[destRow + dx];
                        int inverse = 255 - alpha;
                        int r = (((src >> 16) & 0xFF) * alpha + ((dst >> 16) & 0xFF) * inverse) / 255;
                        int g = (((src >> 8) & 0xFF) * alpha + ((dst >> 8) & 0xFF) * inverse) / 255;
                        int b = ((src & 0xFF) * alpha + (dst & 0xFF) * inverse) / 255;
                        _pixels[destRow + dx] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                    }
                }
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        /// <summary>Copies the framebuffer into the bitmap and draws it to the target.</summary>
        public void Present(Graphics graphics, int destinationWidth, int destinationHeight)
        {
            if (graphics == null || _disposed) return;

            unsafe
            {
                fixed (int* source = _pixels)
                {
                    // The bitmap is Format32bppPArgb, which is byte-identical to our int[]
                    // on little-endian hardware, so this is one straight copy. .NET 4.0 has
                    // no Buffer.MemoryCopy, so the copy goes through Marshal.
                    long bytes = (long)Width * Height * 4;
                    CopyMemory((void*)_scan0, source, bytes);
                }
            }

            _bitmap.UnlockBits(_data);

            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_bitmap, new Rectangle(0, 0, destinationWidth, destinationHeight));

            // Re-lock for the next frame.
            _bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        }

        /// <summary>Writes the framebuffer to a PNG, for the offline render mode and CI.</summary>
        public void SavePng(string path)
        {
            if (_disposed) return;

            unsafe
            {
                fixed (int* source = _pixels)
                {
                    long bytes = (long)Width * Height * 4;
                    CopyMemory((void*)_scan0, source, bytes);
                }
            }

            _bitmap.UnlockBits(_data);
            _bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            _bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _bitmap.UnlockBits(_data); } catch (Exception) { }
            _bitmap.Dispose();
        }
    }
}
