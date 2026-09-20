using MicroFocus.Core.Imaging;

namespace MicroFocus.Core.Services;

public sealed record PlannedFrame(
    Frame Frame,
    Affine PixelToCanvas,
    Affine CanvasToPixel,
    bool ScaleInferred);

public sealed class Frame
{
    public required FrameRecord Record { get; init; }
    public required PnmCodec.PnmImage Image { get; init; }
}

public sealed class CanvasPlan
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double PixelSizeUm { get; init; }
    public required double OriginXUm { get; init; }
    public required double OriginYUm { get; init; }
    public required List<PlannedFrame> Frames { get; init; }
    public required List<string> ExcludedFrameIds { get; init; }
    public required List<AlignmentDiagnostic> Diagnostics { get; init; }
}

public static class AlignmentPlanner
{
    public const string RuleVersion = "alignment-1.0";

    /// <summary>Nominal sensor pixel size (um) at common objective magnifications.</summary>
    public static readonly Dictionary<double, double> NominalPixelSizeUm = new()
    {
        [4] = 1.6,
        [10] = 0.65,
        [20] = 0.32,
        [40] = 0.16,
        [63] = 0.10,
        [100] = 0.065,
    };

    public const double MagnificationTolerance = 0.02; // 2%

    /// <summary>Resolve scale (um/pixel) for a frame. Returns null when no scale can be defended.</summary>
    public static double? ResolvePixelSizeUm(FrameRecord f, double? inferredMagnification,
        List<AlignmentDiagnostic> diag)
    {
        if (f.PixelSizeUm is > 0) return f.PixelSizeUm;

        double? mag = f.Magnification ?? inferredMagnification;
        if (mag is null)
        {
            diag.Add(new AlignmentDiagnostic
            {
                Code = "SCALE_MISSING",
                Severity = "warning",
                FrameId = f.FrameId,
                Message = $"帧 {f.OriginalFileName} 既没有像素尺寸，也没有可用于推断的倍率，无法定位。",
            });
            return null;
        }

        double? key = NominalPixelSizeUm.Keys
            .OrderBy(k => Math.Abs(k - mag.Value))
            .FirstOrDefault(k => Math.Abs(k - mag.Value) / mag.Value < 0.12);
        if (key is 0 or null)
        {
            diag.Add(new AlignmentDiagnostic
            {
                Code = "SCALE_TABLE_MISS",
                Severity = "warning",
                FrameId = f.FrameId,
                Message = $"倍率 {mag:G4} 不在标定表中，无法推断像素尺寸。",
            });
            return null;
        }

        double px = NominalPixelSizeUm[key.Value];
        diag.Add(new AlignmentDiagnostic
        {
            Code = "PIXEL_SIZE_INFERRED",
            Severity = "info",
            FrameId = f.FrameId,
            Message = $"帧 {f.OriginalFileName} 未声明像素尺寸，按倍率 {key:G4}x 推断为 {px} um/px（该区域可信度降低）。",
        });
        return px;
    }

    public static CanvasPlan? Plan(IReadOnlyList<Frame> frames, int tileSize)
    {
        var diag = new List<AlignmentDiagnostic>();
        var excluded = new List<string>();

        // 1) infer magnification from explicit majority when missing
        var explicitMags = frames
            .Where(f => f.Record.Magnification is > 0 && !f.Record.MagnificationInferred)
            .Select(f => f.Record.Magnification!.Value)
            .OrderBy(v => v)
            .ToList();
        double? inferredMag = explicitMags.Count > 0 ? explicitMags[explicitMags.Count / 2] : null;
        if (explicitMags.Count >= 2)
        {
            double min = explicitMags.Min(), max = explicitMags.Max();
            double rel = (max - min) / Math.Max(1e-9, Math.Abs(min));
            if (rel > MagnificationTolerance)
            {
                diag.Add(new AlignmentDiagnostic
                {
                    Code = "MAGNIFICATION_CONFLICT",
                    Severity = "warning",
                    Message = $"帧之间倍率矛盾：{min:G4}x 与 {max:G4}x（相差 {rel:P0}），冲突覆盖区域将画成不可信。",
                });
            }
        }

        // 2) stage coordinate validity; units may be missing => untrusted
        var eligible = new List<(Frame f, double pxUm, bool inferred)>();
        foreach (var fr in frames)
        {
            var rec = fr.Record;
            if (!rec.StageCoordinatesTrusted || rec.StageXUm is null || rec.StageYUm is null)
            {
                excluded.Add(rec.FrameId);
                diag.Add(new AlignmentDiagnostic
                {
                    Code = "STAGE_UNIT_MISSING",
                    Severity = "warning",
                    FrameId = rec.FrameId,
                    Message = $"帧 {rec.OriginalFileName} 缺少载台坐标或坐标单位，不参与对齐，已排除。",
                });
                continue;
            }
            double? pxUm = ResolvePixelSizeUm(rec, inferredMag, diag);
        bool scaleInferred = pxUm.HasValue && rec.PixelSizeUm is not > 0;
            if (pxUm is null or <= 0)
            {
                excluded.Add(rec.FrameId);
                continue;
            }
            eligible.Add((fr, pxUm.Value, scaleInferred));
        }

        if (eligible.Count == 0)
        {
            diag.Add(new AlignmentDiagnostic
            {
                Code = "NO_ALIGNABLE_FRAME",
                Severity = "error",
                Message = "没有任何帧具备可信坐标与可确定的像素尺寸，无法生成合成图。",
            });
            return new CanvasPlan
            {
                Width = 0, Height = 0, PixelSizeUm = 0, OriginXUm = 0, OriginYUm = 0,
                Frames = new(), ExcludedFrameIds = excluded, Diagnostics = diag,
            };
        }

        if (eligible.Count == 1)
            diag.Add(new AlignmentDiagnostic
            {
                Code = "SINGLE_FRAME",
                Severity = "warning",
                Message = "只有一帧可对齐：画布只覆盖该帧范围，全部标记为“单帧不可交叉验证”。",
            });

        // 3) canvas at the finest declared/inferred scale, union bounds in stage coordinates
        double canvasPxUm = eligible.Min(e => e.pxUm);
        double minXUm = double.PositiveInfinity, minYUm = double.PositiveInfinity;
        double maxXUm = double.NegativeInfinity, maxYUm = double.NegativeInfinity;
        foreach (var (fr, pxUm, _) in eligible)
        {
            var r = fr.Record;
            double wUm = fr.Image.Width * pxUm, hUm = fr.Image.Height * pxUm;
            minXUm = Math.Min(minXUm, r.StageXUm!.Value);
            minYUm = Math.Min(minYUm, r.StageYUm!.Value);
            maxXUm = Math.Max(maxXUm, r.StageXUm!.Value + wUm);
            maxYUm = Math.Max(maxYUm, r.StageYUm!.Value + hUm);
        }

        int cw = Math.Max(1, (int)Math.Ceiling((maxXUm - minXUm) / canvasPxUm));
        int ch = Math.Max(1, (int)Math.Ceiling((maxYUm - minYUm) / canvasPxUm));

        var planned = new List<PlannedFrame>();
        foreach (var (fr, pxUm, inferred) in eligible)
        {
            var r = fr.Record;
            var fwd = Affine.FrameToCanvas(pxUm, r.StageXUm!.Value, r.StageYUm!.Value, canvasPxUm, minXUm, minYUm);
            planned.Add(new PlannedFrame(fr, fwd, fwd.Inverse(), inferred));
        }

        diag.Add(new AlignmentDiagnostic
        {
            Code = "CANVAS_PLANNED",
            Severity = "info",
            Message = $"画布 {cw}x{ch} @ {canvasPxUm:G4} um/px，覆盖载台 X[{minXUm:G4},{maxXUm:G4}] Y[{minYUm:G4},{maxYUm:G4}] um，仅取帧并集，不做边缘外推。",
        });

        return new CanvasPlan
        {
            Width = cw,
            Height = ch,
            PixelSizeUm = canvasPxUm,
            OriginXUm = minXUm,
            OriginYUm = minYUm,
            Frames = planned,
            ExcludedFrameIds = excluded,
            Diagnostics = diag,
        };
    }
}
