using MicroFocus.Core.Imaging;

namespace MicroFocus.Core.Tests;

internal static class TestData
{
    public static byte[] Pgm(int w, int h, Func<int, int, byte> pixel)
    {
        var bytes = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bytes[y * w + x] = pixel(x, y);
        return PnmCodec.EncodeP5(bytes, w, h);
    }

    /// <summary>Synthetic "detail" frame: smooth gradient plus a sharp checker patch at given tile coords.</summary>
    public static byte[] DetailFrame(int w, int h, int patchX, int patchY, int patchSize, int seed = 0)
    {
        return Pgm(w, h, (x, y) =>
        {
            int v = 40 + (x + y) % 30 + seed * 5;
            if (x >= patchX && x < patchX + patchSize && y >= patchY && y < patchY + patchSize)
                v = ((x + y) & 3) < 2 ? 230 : 20;
            return (byte)Math.Clamp(v, 0, 255);
        });
    }

    public static byte[] FlatFrame(int w, int h, byte value = 90) =>
        Pgm(w, h, (_, _) => value);
}
