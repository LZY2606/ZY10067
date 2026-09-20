using MicroStack.Core.Models;

namespace MicroStack.Core.Engine;

/// <summary>Stage-coordinate -> canvas-pixel layout, including untrusted placement diagnostics.</summary>
public static class Geometry
{
    public sealed record LayoutResult(
        List<FrameLayout> Layouts,
        double ReferenceMagnification,
        int CanvasWidth,
        int CanvasHeight,
        List<FrameDiagnostic> Diagnostics);

    public static double PixelSizeUm(double magnification, EngineParameters p) =>
        p.PixelScaleFactorUm / magnification;

    public static LayoutResult BuildLayout(IReadOnlyList<FrameRef> frames, EngineParameters p)
    {
        var layouts = new List<FrameLayout>();
        var diagnostics = new List<FrameDiagnostic>();

        var calibrated = frames.Where(f => !f.MagnificationMissing && f.Stage.HasMicrons).ToList();
        double referenceMag = 0;
        if (calibrated.Count > 0)
        {
            // Reference = the magnification with the most frames; ties resolved by the
            // magnification that appears first in the user's upload order (stable, no silent rescale).
            var counts = calibrated.GroupBy(f => f.Magnification)
                .ToDictionary(g => g.Key, g => g.Count());
            int bestCount = -1;
            foreach (var f in calibrated)
            {
                int c = counts[f.Magnification];
                if (c > bestCount) { bestCount = c; referenceMag = f.Magnification; }
            }
        }
        double refPixelUm = referenceMag == 0 ? p.PixelScaleFactorUm : PixelSizeUm(referenceMag, p);

        foreach (var f in frames)
        {
            if (f.Stage.X is null || f.Stage.Y is null)
            {
                layouts.Add(Unplaced(f, "UnitsMissing"));
                diagnostics.Add(new FrameDiagnostic(f.EvidenceId, "UnitsMissing",
                    "stage coordinates are absent; frame cannot be aligned", false));
                continue;
            }
            if (!StagePosition.IsMicron(f.Stage.XUnit) || !StagePosition.IsMicron(f.Stage.YUnit))
            {
                layouts.Add(Unplaced(f, "UnitsMissing"));
                diagnostics.Add(new FrameDiagnostic(f.EvidenceId, "UnitsMissing",
                    $"coordinate unit missing/unrecognized (x='{f.Stage.XUnit ?? "null"}', y='{f.Stage.YUnit ?? "null"}'); refusing silent alignment", false));
                continue;
            }
            if (f.MagnificationMissing)
            {
                layouts.Add(Unplaced(f, "CalibrationMissing"));
                diagnostics.Add(new FrameDiagnostic(f.EvidenceId, "CalibrationMissing",
                    "magnification is missing; cannot calibrate pixel size", false));
                continue;
            }

            bool conflict = referenceMag > 0 &&
                Math.Abs(f.Magnification - referenceMag) / referenceMag > p.MagnificationTolerance;
            double pixelUm = PixelSizeUm(f.Magnification, p);

            // Stage coordinate marks the frame center; canvas y points down, stage y up.
            double originX = (f.Stage.X.Value - (f.Width * pixelUm) / 2) / refPixelUm;
            double originY = -(f.Stage.Y.Value + (f.Height * pixelUm) / 2) / refPixelUm;
            double drawW = f.Width * pixelUm / refPixelUm;
            double drawH = f.Height * pixelUm / refPixelUm;

            layouts.Add(new FrameLayout
            {
                EvidenceId = f.EvidenceId,
                Frame = f,
                PixelSizeUm = pixelUm,
                OriginX = originX,
                OriginY = originY,
                DrawX0 = (int)Math.Floor(originX),
                DrawY0 = (int)Math.Floor(originY),
                DrawX1 = (int)Math.Ceiling(originX + drawW),
                DrawY1 = (int)Math.Ceiling(originY + drawH),
                Placed = true,
                MagnificationConflict = conflict,
                Reason = conflict ? "MagnificationConflict" : null
            });
            diagnostics.Add(new FrameDiagnostic(f.EvidenceId,
                conflict ? "MagnificationConflict" : "PlacementOk",
                conflict
                    ? $"magnification {f.Magnification:g}x conflicts with reference {referenceMag:g}x; footprint drawn as untrusted"
                    : $"aligned at ({originX:f1},{originY:f1}) with {pixelUm:g4} um/px",
                true));
        }

        var placed = layouts.Where(l => l.Placed).ToList();
        int cw, ch;
        if (placed.Count == 0)
        {
            cw = Math.Max(1, frames.Count == 0 ? 1 : frames.Max(f => f.Width));
            ch = Math.Max(1, frames.Count == 0 ? 1 : frames.Max(f => f.Height));
        }
        else
        {
            int x0 = placed.Min(l => l.DrawX0);
            int y0 = placed.Min(l => l.DrawY0);
            int x1 = placed.Max(l => l.DrawX1);
            int y1 = placed.Max(l => l.DrawY1);
            cw = x1 - x0;
            ch = y1 - y0;
            foreach (var l in placed) l.Shift(-x0, -y0);
        }

        return new LayoutResult(layouts, referenceMag, cw, ch, diagnostics);
    }

    private static FrameLayout Unplaced(FrameRef f, string reason) => new()
    {
        EvidenceId = f.EvidenceId,
        Frame = f,
        Placed = false,
        Reason = reason
    };
}
