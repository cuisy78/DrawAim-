namespace DrawAim.Core.Color;

public static class ColorScoreV1
{
    public const string Version = "ColorScoreV1";

    public static ColorScoreResult Score(SrgbColor target, SrgbColor player)
    {
        if (!target.IsFinite)
        {
            throw new ArgumentException("Target color must be finite.", nameof(target));
        }

        if (!player.IsFinite)
        {
            throw new ArgumentException("Player color must be finite.", nameof(player));
        }

        target = target.Clamp();
        player = player.Clamp();
        var targetLab = ColorMath.SrgbToOklab(target);
        var playerLab = ColorMath.SrgbToOklab(player);
        var targetLch = ColorMath.OklabToOklch(targetLab);
        var playerLch = ColorMath.OklabToOklch(playerLab);
        var targetHsv = ColorMath.SrgbToHsv(target);
        var playerHsv = ColorMath.SrgbToHsv(player);

        var deltaE = ColorMath.DeltaEOK(targetLab, playerLab);
        var identical = target == player || deltaE <= 1e-12;
        var similarity = identical ? 100 : 100 * Math.Exp(-0.035 * deltaE);

        var hueDefined =
            targetLch.C >= 0.02 && playerLch.C >= 0.02 &&
            targetLab.L >= 0.05 && playerLab.L >= 0.05;
        var saturationDefined = targetHsv.Value >= 0.01 && playerHsv.Value >= 0.01;

        return new ColorScoreResult(
            Math.Clamp(similarity, 0, 100),
            deltaE,
            100 * (playerLab.L - targetLab.L),
            100 * (playerLch.C - targetLch.C),
            hueDefined
                ? ColorMath.ShortestHueDifference(playerLch.HueDegrees, targetLch.HueDegrees)
                : null,
            100 * (playerLab.A - targetLab.A),
            100 * (playerLab.B - targetLab.B),
            saturationDefined
                ? 100 * (playerHsv.Saturation - targetHsv.Saturation)
                : null,
            100 * (playerHsv.Value - targetHsv.Value));
    }
}
