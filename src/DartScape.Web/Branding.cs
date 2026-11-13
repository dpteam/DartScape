using System;
using System.Globalization;
using System.Text;
using DartScape.Core.Diagnostics;
using DartScape.Game;

namespace DartScape.Web
{
    /// <summary>
    /// Generates <c>/brand.css</c> from the <c>[branding]</c> section of the config.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deployment should be able to look like its owner's project without editing
    /// the stylesheet. The page links <c>brand.css</c> after <c>app.css</c>, and this
    /// class writes the same custom-property names the themes already use, so a single
    /// accent colour cascades through buttons, links, the top bar and the focus rings.
    /// </para>
    /// <para>
    /// Everything is optional. An empty or missing section produces an empty stylesheet,
    /// which means the stock palette вЂ” that is the documented behaviour, and it is why
    /// the file can be linked unconditionally.
    /// </para>
    /// </remarks>
    public static class Branding
    {
        /// <summary>
        /// Builds the stylesheet. Never returns <c>null</c>: an unconfigured deployment
        /// gets a comment-only file rather than a 404, so the browser console stays clean.
        /// </summary>
        public static string BuildStylesheet(ServerConfig config)
        {
            var css = new StringBuilder(1024);

            css.AppendLine("/* DartScape brand overrides, generated from config.ini [branding]. */");
            css.AppendLine("/* Edit the config, not this file: it is rewritten on every request. */");

            if (config == null)
            {
                return css.ToString();
            }

            string accent = Normalise(config.Raw("branding.accent", null));
            string accentDark = Normalise(config.Raw("branding.accentDark", null));
            string background = Normalise(config.Raw("branding.background", null));
            string panel = Normalise(config.Raw("branding.panel", null));

            if (accent == null && accentDark == null && background == null && panel == null)
            {
                css.AppendLine("/* No [branding] section configured; the stock palette applies. */");
                return css.ToString();
            }

            // Derive the missing shades from the one colour the operator gave us, so a
            // single `accent = #c0392b` produces a coherent palette rather than one
            // button that does not match anything.
            if (accent != null && accentDark == null) accentDark = Shade(accent, -0.22);

            foreach (string theme in new[] { "light", "dark", "oled" })
            {
                css.Append("html[data-theme=\"").Append(theme).Append("\"]{");

                if (accent != null)
                {
                    css.Append("--accent:").Append(accent).Append(';');
                    css.Append("--accent-hi:").Append(Shade(accent, 0.16)).Append(';');
                    css.Append("--accent-lo:").Append(Shade(accent, -0.16)).Append(';');
                    css.Append("--link:").Append(Shade(accent, -0.10)).Append(';');
                    css.Append("--link-hi:").Append(Shade(accent, 0.10)).Append(';');
                    css.Append("--accent-soft:").Append(Rgba(accent, 0.12)).Append(';');
                }

                if (accentDark != null)
                {
                    // The top bar is the strongest visual statement on the page.
                    css.Append("--topbar:linear-gradient(").Append(Shade(accent ?? accentDark, 0.08))
                       .Append(", ").Append(accentDark).Append(" 55%, ").Append(Shade(accentDark, -0.12))
                       .Append(");");
                }

                if (background != null) css.Append("--bg:").Append(background).Append(';');
                if (panel != null) css.Append("--panel:").Append(panel).Append(';');

                css.AppendLine("}");
            }

            return css.ToString();
        }

        /// <summary>
        /// Accepts <c>#rrggbb</c>, <c>#rgb</c> and bare hex; returns <c>#rrggbb</c> or
        /// <c>null</c> when the value is not a colour.
        /// </summary>
        /// <remarks>
        /// Validation matters here: the value goes straight into a stylesheet served to
        /// browsers, and a config typo should produce the stock palette rather than a
        /// broken rule or, worse, injected CSS.
        /// </remarks>
        private static string Normalise(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            string text = value.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal)) text = text.Substring(1);

            if (text.Length == 3)
            {
                text = new string(new[]
                {
                    text[0], text[0], text[1], text[1], text[2], text[2]
                });
            }

            if (text.Length != 6) return null;

            foreach (char c in text)
            {
                if (!Uri.IsHexDigit(c)) return null;
            }

            return "#" + text.ToLowerInvariant();
        }

        /// <summary>Lightens (positive) or darkens (negative) a colour by a fraction.</summary>
        private static string Shade(string colour, double amount)
        {
            int r, g, b;
            if (!TryParse(colour, out r, out g, out b)) return colour;

            if (amount >= 0)
            {
                r = (int)Math.Round(r + (255 - r) * amount);
                g = (int)Math.Round(g + (255 - g) * amount);
                b = (int)Math.Round(b + (255 - b) * amount);
            }
            else
            {
                double factor = 1.0 + amount;
                r = (int)Math.Round(r * factor);
                g = (int)Math.Round(g * factor);
                b = (int)Math.Round(b * factor);
            }

            return Format(r, g, b);
        }

        /// <summary>Renders a colour with an alpha channel as <c>rgba()</c>.</summary>
        private static string Rgba(string colour, double alpha)
        {
            int r, g, b;
            if (!TryParse(colour, out r, out g, out b)) return "rgba(0,0,0," + alpha.ToString("0.##", CultureInfo.InvariantCulture) + ")";

            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3})",
                r, g, b, alpha.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private static bool TryParse(string colour, out int r, out int g, out int b)
        {
            r = g = b = 0;

            if (string.IsNullOrEmpty(colour) || colour.Length != 7 || colour[0] != '#') return false;

            return int.TryParse(colour.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && int.TryParse(colour.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && int.TryParse(colour.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        private static string Format(int r, int g, int b)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}",
                Math.Max(0, Math.Min(255, r)),
                Math.Max(0, Math.Min(255, g)),
                Math.Max(0, Math.Min(255, b)));
        }
    }
}
