using MicroStack.Core.Imaging;

namespace MicroStack.Core.Engine;

/// <summary>Sobel (Tenengrad) local sharpness maps.</summary>
public static class Sharpness
{
    public static double[,] TenengradMap(PngImage image)
    {
        int w = image.Width, h = image.Height;
        var g = new double[w, h];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                double tl = image.GrayAt(x - 1, y - 1), tc = image.GrayAt(x, y - 1), tr = image.GrayAt(x + 1, y - 1);
                double ml = image.GrayAt(x - 1, y), mr = image.GrayAt(x + 1, y);
                double bl = image.GrayAt(x - 1, y + 1), bc = image.GrayAt(x, y + 1), br = image.GrayAt(x + 1, y + 1);
                double gx = -tl - 2 * ml - bl + tr + 2 * mr + br;
                double gy = -tl - 2 * tc - tr + bl + 2 * bc + br;
                g[x, y] = gx * gx + gy * gy;
            }
        }
        return g;
    }

    /// <summary>Box-averaged sharpness via summed-area table; radius clamps at borders.</summary>
    public static double[,] WindowAverage(double[,] g, int radius)
    {
        int w = g.GetLength(0), h = g.GetLength(1);
        var sat = new double[w + 1, h + 1];
        for (int y = 1; y <= h; y++)
        {
            double rowSum = 0;
            for (int x = 1; x <= w; x++)
            {
                rowSum += g[x - 1, y - 1];
                sat[x, y] = sat[x, y - 1] + rowSum;
            }
        }
        var avg = new double[w, h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius) + 1;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius) + 1;
                double sum = sat[x1, y1] - sat[x0, y1] - sat[x1, y0] + sat[x0, y0];
                avg[x, y] = sum / ((x1 - x0) * (y1 - y0));
            }
        }
        return avg;
    }
}
