namespace MicroFocus.Core.Imaging;

/// <summary>
/// 2x3 affine transform for 2D points (uniform scale + translation in this application):
/// [a b tx; c d ty].
/// </summary>
public readonly struct Affine
{
    public double A { get; }
    public double B { get; }
    public double C { get; }
    public double D { get; }
    public double Tx { get; }
    public double Ty { get; }

    public Affine(double a, double b, double c, double d, double tx, double ty)
    {
        A = a; B = b; C = c; D = d; Tx = tx; Ty = ty;
    }

    public (double x, double y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);

    public double[][] ToMatrix() => new[]
    {
        new[] { A, B, Tx },
        new[] { C, D, Ty },
    };

    public static Affine FromMatrix(double[][] m) => new(m[0][0], m[0][1], m[1][0], m[1][1], m[0][2], m[1][2]);

    public Affine Inverse()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-18) throw new InvalidOperationException("singular affine transform");
        return new Affine(
            D / det, -B / det, -C / det, A / det,
            (B * Ty - D * Tx) / det,
            (C * Tx - A * Ty) / det);
    }

    /// <summary>Frame-pixel coordinates to canvas-pixel coordinates.</summary>
    public static Affine FrameToCanvas(double framePixelSizeUm, double frameOriginXUm, double frameOriginYUm,
        double canvasPixelSizeUm, double canvasOriginXUm, double canvasOriginYUm)
    {
        double s = framePixelSizeUm / canvasPixelSizeUm;
        return new Affine(s, 0, 0, s,
            (frameOriginXUm - canvasOriginXUm) / canvasPixelSizeUm,
            (frameOriginYUm - canvasOriginYUm) / canvasPixelSizeUm);
    }
}
