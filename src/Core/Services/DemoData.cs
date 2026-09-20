using MicroStack.Core.Imaging;

namespace MicroStack.Core.Services;

/// <summary>
/// Deterministic synthetic z-stack used by the in-app demo and tests:
/// three full-field frames at different focal depths (sharp features sit in different
/// horizontal bands), one partial re-shot frame (top-left region), and optional dust.
/// </summary>
public static class DemoData
{
    public sealed record DemoFrame(
        string Filename,
        byte[] Png,
        double Magnification,
        double StageX,
        double StageY,
        string StageUnit,
        double ZUm,
        string CapturedAt);

    public const int FullSize = 192;
    public const int PartialSize = 96;

    public static List<DemoFrame> Build(int tileSize = 64)
    {
        // Pixel size is 10um / 10x = 1um/px; stages chosen so frames overlap in the same field.
        double umPerPx = 1.0;
        var t0 = "2026-09-20T01:00:00Z";
        var frames = new List<DemoFrame>
        {
            MakeFull("z0.png", focusBand: 0, z: 0, dust: false, stageX: 0, stageY: 0, t0),
            MakeFull("z5.png", focusBand: 1, z: 5, dust: true, stageX: 0, stageY: 0, "2026-09-20T01:00:05Z"),
            MakeFull("z10.png", focusBand: 2, z: 10, dust: false, stageX: 0, stageY: 0, "2026-09-20T01:00:10Z"),
        };

        // Partial re-shot of the top-left quadrant, at z=5, shifted stage within the field.
        var partial = MakePartial("patch-z5.png", "2026-09-20T01:00:20Z");
        frames.Add(partial);
        return frames;
    }

    private static DemoFrame MakeFull(string name, int focusBand, double z, bool dust,
        double stageX, double stageY, string capturedAt)
    {
        int n = FullSize;
        var img = new PngImage(n, n, 1);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int band = y * 3 / n;
            bool sharpHere = band == focusBand;
            double v = 40;
            double f1 = Pattern(x, y, 7, 0.0);
            double f2 = Pattern(x, y, 5, 2.3);
            double detail = (f1 + f2) * 0.5;
            v += sharpHere ? detail * 150 : detail * 25;
            img.SetGray(x, y, (byte)Math.Clamp(v, 0, 255));
        }
        if (dust) DrawDust(img, 130, 60, 4);
        return new DemoFrame(name, Png.Encode(img), 10, stageX, stageY, "um", z, capturedAt);
    }

    private static DemoFrame MakePartial(string name, string capturedAt)
    {
        int n = PartialSize;
        var img = new PngImage(n, n, 1);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            double v = 40;
            double detail = (Pattern(x, y, 7, 0) + Pattern(x, y, 5, 2.3)) * 0.5;
            v += detail * 150;
            img.SetGray(x, y, (byte)Math.Clamp(v, 0, 255));
        }
        // Stage places this so it covers the top-left portion of the full field.
        // Full frame spans -96..96 um in both axes. Partial spans 96um; center at (-48,-48).
        return new DemoFrame(name, Png.Encode(img), 10, -48, 48, "um", 5, capturedAt);
    }

    private static double Pattern(int x, int y, int period, double phase)
    {
        double s = (Math.Sin((x + phase) * 2 * Math.PI / period) +
                    Math.Cos((y - phase) * 2 * Math.PI / period)) / 2;
        // add small dots for high-frequency content
        s += Math.Sin(x * 0.9 + y * 0.7 + phase) * 0.3;
        return Math.Clamp(s / 1.3, -1, 1) * 0.5 + 0.5;
    }

    private static void DrawDust(PngImage img, int cx, int cy, int r)
    {
        for (int y = cy - r; y <= cy + r; y++)
        for (int x = cx - r; x <= cx + r; x++)
        {
            if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                img.SetGray(x, y, 18);
        }
    }
}
