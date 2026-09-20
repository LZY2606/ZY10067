namespace MicroStack.Core.Engine;

/// <summary>
/// Diagnostic overlay (8-bit PNG) channel codes. Zero = trusted.
/// Rendered as an indexed visual mask for the web UI and carried in exports.
/// </summary>
public static class OverlayCodes
{
    public const byte Trusted = 0;
    public const byte NoCoverage = 1;
    public const byte InsufficientAlignment = 2;
    public const byte ExtrapolatedEdge = 3;
    public const byte MagnificationConflict = 4;
    public const byte CalibrationMissing = 5;

    public static readonly Dictionary<byte, string> Names = new()
    {
        [Trusted] = "trusted",
        [NoCoverage] = "no-coverage",
        [InsufficientAlignment] = "insufficient-alignment",
        [ExtrapolatedEdge] = "extrapolated-edge",
        [MagnificationConflict] = "magnification-conflict",
        [CalibrationMissing] = "calibration-missing",
    };
}

/// <summary>Source-index mask value meaning "no source frame".</summary>
public static class MaskCodes
{
    public const byte NoSource = 255;
}
