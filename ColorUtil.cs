using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynaIsland;

internal static class ColorUtil
{
    public static readonly Color Neutral = Color.FromRgb(0xE8, 0xE8, 0xEC);

    /// <summary>
    /// Picks a vivid accent from album art (favouring saturated, mid-lightness pixels)
    /// and lifts it so it reads well on the black island.
    /// </summary>
    public static Color AccentFrom(BitmapSource source)
    {
        var scaled = new TransformedBitmap(source,
            new ScaleTransform(16.0 / source.PixelWidth, 16.0 / source.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        double r = 0, g = 0, b = 0, total = 0, satSum = 0;
        int count = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
            var (_, s, l) = ToHsl(pr, pg, pb);
            double weight = s * s * Math.Max(0.05, 1 - Math.Abs(l - 0.5) * 1.8) + 0.002;
            r += pr * weight; g += pg * weight; b += pb * weight;
            total += weight;
            satSum += s;
            count++;
        }

        if (count == 0 || satSum / count < 0.10) return Neutral;

        var (hue, sat, light) = ToHsl(r / total, g / total, b / total);
        sat = Math.Clamp(sat * 1.15, 0.45, 0.9);
        light = Math.Clamp(light, 0.62, 0.74);
        var (rr, gg, bb) = FromHsl(hue, sat, light);
        return Color.FromRgb((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
    }

    private static (double h, double s, double l) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 1e-6)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static (double r, double g, double b) FromHsl(double h, double s, double l)
    {
        if (s <= 0) return (l, l, l);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return (Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
