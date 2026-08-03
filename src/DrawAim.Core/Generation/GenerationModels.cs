using DrawAim.Core.Color;
using DrawAim.Core.Geometry;

namespace DrawAim.Core.Generation;

public enum ExerciseMode
{
    LineTrace = 1,
    CompositionCopy = 2,
    ColorMatch = 3,
}

public readonly record struct GenerationKey(
    string GeneratorVersion,
    ExerciseMode Mode,
    ulong BaseSeed,
    int ExerciseIndex,
    string SettingsFingerprint,
    double CanvasWidthDip,
    double CanvasHeightDip);

public sealed record GenerationError(string Code, string Message, string? Field = null);

public sealed class GenerationResult<T>
{
    private GenerationResult(
        bool isSuccess,
        T value,
        GenerationError? error,
        bool usedFallback)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        UsedFallback = usedFallback;
    }

    public bool IsSuccess { get; }

    public T Value { get; }

    public GenerationError? Error { get; }

    public bool UsedFallback { get; }

    public static GenerationResult<T> Success(T value, bool usedFallback = false) =>
        new(true, value, null, usedFallback);

    public static GenerationResult<T> Failure(GenerationError error) =>
        new(false, default!, error ?? throw new ArgumentNullException(nameof(error)), false);
}

public sealed record LineGenerationSettings
{
    public static LineGenerationSettings Default { get; } = new();

    public double StraightWeight { get; init; } = 1;
    public double CShapeWeight { get; init; } = 1;
    public double SShapeWeight { get; init; } = 1;
    public double MinimumLengthRatio { get; init; } = 0.30;
    public double MaximumLengthRatio { get; init; } = 0.72;
    public double MinimumCurvatureRatio { get; init; } = 0.08;
    public double MaximumCurvatureRatio { get; init; } = 0.28;
    public double MinimumDirectionDegrees { get; init; }
    public double MaximumDirectionDegrees { get; init; } = 360;
    public double SafeMarginRatio { get; init; } = 0.06;
    public int Difficulty { get; init; } = 5;
    public int MaximumAttempts { get; init; } = 64;
}

public sealed record MultiLineGenerationSettings
{
    public static MultiLineGenerationSettings Default { get; } = new();

    public int MinimumLineCount { get; init; } = 3;
    public int MaximumLineCount { get; init; } = 5;
    public double StraightWeight { get; init; } = 1;
    public double CShapeWeight { get; init; } = 1;
    public double SShapeWeight { get; init; } = 1;
    public double MinimumLengthRatio { get; init; } = 0.18;
    public double MaximumLengthRatio { get; init; } = 0.52;
    public double MinimumCurvatureRatio { get; init; } = 0.06;
    public double MaximumCurvatureRatio { get; init; } = 0.24;
    public double SafeMarginRatio { get; init; } = 0.04;
    public double MinimumSeparationRatio { get; init; } = 0.025;
    public bool AllowIntersections { get; init; }
    public int Difficulty { get; init; } = 5;
    public int MaximumAttemptsPerLine { get; init; } = 96;
}

public sealed class MultiLineExercise
{
    private readonly IReadOnlyList<TargetCurve> _lines;

    public MultiLineExercise(IEnumerable<TargetCurve> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _lines = Array.AsReadOnly(lines.ToArray());
    }

    public IReadOnlyList<TargetCurve> Lines => _lines;
}

public sealed record ColorGenerationSettings
{
    public static ColorGenerationSettings Default { get; } = new();

    public double MinimumLightness { get; init; } = 0.12;
    public double MaximumLightness { get; init; } = 0.92;
    public double MinimumChroma { get; init; } = 0.025;
    public double MaximumChroma { get; init; } = 0.30;
    public bool IncludeNearWhite { get; init; }
    public bool IncludeNearBlack { get; init; }
    public bool IncludeLowChroma { get; init; } = true;
    public int Difficulty { get; init; } = 5;
    public SrgbColor? PreviousColor { get; init; }
    public double MinimumPreviousDeltaE { get; init; }
    public int MaximumAttempts { get; init; } = 256;
}

public sealed record ColorTarget(SrgbColor Srgb, OklabColor Oklab, OklchColor Oklch);
