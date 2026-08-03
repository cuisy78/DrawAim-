namespace DrawAim.Core.Color;

public static class ColorMath
{
    public static OklabColor SrgbToOklab(SrgbColor color)
    {
        color = color.Clamp();
        var red = SrgbToLinear(color.R);
        var green = SrgbToLinear(color.G);
        var blue = SrgbToLinear(color.B);

        var l = (0.4122214708 * red) + (0.5363325363 * green) + (0.0514459929 * blue);
        var m = (0.2119034982 * red) + (0.6806995451 * green) + (0.1073969566 * blue);
        var s = (0.0883024619 * red) + (0.2817188376 * green) + (0.6299787005 * blue);

        var lRoot = Math.Cbrt(l);
        var mRoot = Math.Cbrt(m);
        var sRoot = Math.Cbrt(s);

        return new OklabColor(
            (0.2104542553 * lRoot) + (0.7936177850 * mRoot) - (0.0040720468 * sRoot),
            (1.9779984951 * lRoot) - (2.4285922050 * mRoot) + (0.4505937099 * sRoot),
            (0.0259040371 * lRoot) + (0.7827717662 * mRoot) - (0.8086757660 * sRoot));
    }

    public static SrgbColor OklabToSrgb(OklabColor color, bool clamp = true)
    {
        var lRoot = color.L + (0.3963377774 * color.A) + (0.2158037573 * color.B);
        var mRoot = color.L - (0.1055613458 * color.A) - (0.0638541728 * color.B);
        var sRoot = color.L - (0.0894841775 * color.A) - (1.2914855480 * color.B);

        var l = lRoot * lRoot * lRoot;
        var m = mRoot * mRoot * mRoot;
        var s = sRoot * sRoot * sRoot;

        var red = (+4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
        var green = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
        var blue = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);

        var result = new SrgbColor(
            LinearToSrgb(red),
            LinearToSrgb(green),
            LinearToSrgb(blue));
        return clamp ? result.Clamp() : result;
    }

    public static OklchColor OklabToOklch(OklabColor color)
    {
        var chroma = Math.Sqrt((color.A * color.A) + (color.B * color.B));
        var hue = NormalizeHue(Math.Atan2(color.B, color.A) * 180 / Math.PI);
        return new OklchColor(color.L, chroma, hue);
    }

    public static OklabColor OklchToOklab(OklchColor color)
    {
        var radians = NormalizeHue(color.HueDegrees) * Math.PI / 180;
        return new OklabColor(
            color.L,
            color.C * Math.Cos(radians),
            color.C * Math.Sin(radians));
    }

    public static HsvColor SrgbToHsv(SrgbColor color)
    {
        color = color.Clamp();
        var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
        var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
        var delta = maximum - minimum;
        var hue = 0.0;

        if (delta > 1e-12)
        {
            if (maximum == color.R)
            {
                hue = 60 * (((color.G - color.B) / delta) % 6);
            }
            else if (maximum == color.G)
            {
                hue = 60 * (((color.B - color.R) / delta) + 2);
            }
            else
            {
                hue = 60 * (((color.R - color.G) / delta) + 4);
            }
        }

        var saturation = maximum <= 1e-12 ? 0 : delta / maximum;
        return new HsvColor(NormalizeHue(hue), saturation, maximum);
    }

    public static SrgbColor HsvToSrgb(HsvColor color)
    {
        var hue = NormalizeHue(color.HueDegrees);
        var saturation = Math.Clamp(color.Saturation, 0, 1);
        var value = Math.Clamp(color.Value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var offset = value - chroma;

        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };

        return new SrgbColor(red + offset, green + offset, blue + offset).Clamp();
    }

    public static double DeltaEOK(OklabColor first, OklabColor second)
    {
        var deltaL = first.L - second.L;
        var deltaA = first.A - second.A;
        var deltaB = first.B - second.B;
        return 100 * Math.Sqrt(
            (deltaL * deltaL) +
            (deltaA * deltaA) +
            (deltaB * deltaB));
    }

    public static double ShortestHueDifference(double playerHue, double targetHue)
    {
        var difference = NormalizeHue(playerHue) - NormalizeHue(targetHue);
        if (difference > 180)
        {
            difference -= 360;
        }
        else if (difference <= -180)
        {
            difference += 360;
        }

        return difference;
    }

    public static double NormalizeHue(double hue)
    {
        if (!double.IsFinite(hue))
        {
            return 0;
        }

        var normalized = hue % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static double SrgbToLinear(double channel) =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double channel) =>
        channel <= 0.0031308
            ? 12.92 * channel
            : (1.055 * Math.Pow(Math.Max(channel, 0), 1 / 2.4)) - 0.055;
}
