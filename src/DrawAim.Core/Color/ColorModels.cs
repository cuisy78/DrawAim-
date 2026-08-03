namespace DrawAim.Core.Color;

public readonly record struct SrgbColor(double R, double G, double B)
{
    public bool IsFinite => double.IsFinite(R) && double.IsFinite(G) && double.IsFinite(B);

    public bool IsInGamut(double epsilon = 1e-10) =>
        IsFinite &&
        R >= -epsilon && R <= 1 + epsilon &&
        G >= -epsilon && G <= 1 + epsilon &&
        B >= -epsilon && B <= 1 + epsilon;

    public SrgbColor Clamp() => new(
        Math.Clamp(R, 0, 1),
        Math.Clamp(G, 0, 1),
        Math.Clamp(B, 0, 1));
}

public readonly record struct HsvColor(double HueDegrees, double Saturation, double Value)
{
    public bool IsFinite =>
        double.IsFinite(HueDegrees) &&
        double.IsFinite(Saturation) &&
        double.IsFinite(Value);
}

public readonly record struct OklabColor(double L, double A, double B)
{
    public bool IsFinite => double.IsFinite(L) && double.IsFinite(A) && double.IsFinite(B);
}

public readonly record struct OklchColor(double L, double C, double HueDegrees)
{
    public bool IsFinite =>
        double.IsFinite(L) && double.IsFinite(C) && double.IsFinite(HueDegrees);
}

public sealed record ColorScoreResult(
    double Similarity,
    double DeltaEOK,
    double DeltaLightness,
    double DeltaChroma,
    double? DeltaHueDegrees,
    double DeltaA,
    double DeltaB,
    double? DeltaHsvSaturation,
    double DeltaHsvValue)
{
    public const string ScoringVersion = "ColorScoreV1";

    public bool HueIsDefined => DeltaHueDegrees.HasValue;

    public bool HsvSaturationIsDefined => DeltaHsvSaturation.HasValue;
}
