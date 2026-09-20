namespace MicroFocus.Web;

/// <summary>Renders trust/source overlays on top of the grayscale composite for provenance visualization.</summary>
public static class TrustOverlay
{
    // indexed by TrustCode byte value
    private static readonly (byte r, byte g, byte b)[] Palette =
    {
        (0, 200, 0),     // Trusted - green
        (255, 200, 0),   // SingleFrame - amber
        (255, 140, 0),   // ScaleMissing - orange
        (255, 0, 0),     // MagnificationConflict - red
        (120, 0, 0),     // NoCoverage - dark red
        (0, 140, 255),   // ManualOverride - blue
    };

    public static byte[] Render(byte[] gray, byte[] trust, int w, int h)
    {
        var rgb = new byte[w * (long)h * 3];
        for (int i = 0; i < gray.Length; i++)
        {
            var (r, g, b) = Palette[Math.Min(trust[i], (byte)5)];
            rgb[i * 3] = Blend(gray[i], r);
            rgb[i * 3 + 1] = Blend(gray[i], g);
            rgb[i * 3 + 2] = Blend(gray[i], b);
        }
        return rgb;
    }

    private static byte Blend(byte baseGray, byte tint)
    {
        int v = (baseGray * 35 + tint * 65) / 100;
        return (byte)Math.Clamp(v, 0, 255);
    }
}

public static class SourceOverlay
{
    private static readonly (byte r, byte g, byte b)[] Colors =
    {
        (230, 25, 75), (60, 180, 75), (255, 225, 25), (0, 130, 200),
        (245, 130, 48), (145, 30, 180), (70, 240, 240), (240, 50, 230),
        (210, 245, 60), (250, 190, 190), (0, 128, 128), (230, 190, 255),
        (170, 110, 40), (255, 250, 200), (128, 0, 0), (170, 255, 195),
    };

    public static byte[] Render(byte[] gray, ushort[] source, int w, int h)
    {
        var rgb = new byte[w * (long)h * 3];
        for (int i = 0; i < gray.Length; i++)
        {
            ushort s = source[i];
            if (s == 0)
            {
                rgb[i * 3] = 40; rgb[i * 3 + 1] = 0; rgb[i * 3 + 2] = 0;
                continue;
            }
            var (r, g, b) = Colors[(s - 1) % Colors.Length];
            rgb[i * 3] = Blend(gray[i], r);
            rgb[i * 3 + 1] = Blend(gray[i], g);
            rgb[i * 3 + 2] = Blend(gray[i], b);
        }
        return rgb;
    }

    private static byte Blend(byte baseGray, byte tint) =>
        (byte)Math.Clamp((baseGray * 35 + tint * 65) / 100, 0, 255);
}
