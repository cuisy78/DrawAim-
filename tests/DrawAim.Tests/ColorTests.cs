using DrawAim.Core.Color;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> ColorTests()
    {
        yield return new TestCase("Color/OKLab 黑白红黄金值", OklabGoldenValues);
        yield return new TestCase("Color/sRGB 与 OKLab 往返", OklabRoundTrip);
        yield return new TestCase("Color/HSV 六角与往返", HsvPrimaryColorsAndRoundTrip);
        yield return new TestCase("Color/相同颜色严格 100", IdenticalColorScoresExactlyOneHundred);
        yield return new TestCase("Color/感知距离增大分数单调下降", SimilarityMonotonicallyDecreases);
        yield return new TestCase("Color/色相差正确环绕 0 与 360", HueDifferenceWrapsCorrectly);
        yield return new TestCase("Color/灰色不报告虚假色相", GrayDoesNotReportHue);
        yield return new TestCase("Color/近黑不报告不稳定饱和度", NearBlackDoesNotReportSaturation);
        yield return new TestCase("Color/报告 HSV 饱和度与明度差", HsvSaturationAndValueAreReported);
        yield return new TestCase("Color/ΔE 与各维度均为有限数", ColorReportIsFinite);
    }

    private static void OklabGoldenValues()
    {
        var black = ColorMath.SrgbToOklab(new SrgbColor(0, 0, 0));
        AssertEx.Near(0, black.L, 1e-12);
        AssertEx.Near(0, black.A, 1e-12);
        AssertEx.Near(0, black.B, 1e-12);

        var white = ColorMath.SrgbToOklab(new SrgbColor(1, 1, 1));
        AssertEx.Near(1, white.L, 1e-7);
        AssertEx.Near(0, white.A, 1e-7);
        AssertEx.Near(0, white.B, 1e-7);

        var red = ColorMath.SrgbToOklab(new SrgbColor(1, 0, 0));
        AssertEx.Near(0.62795536, red.L, 1e-7);
        AssertEx.Near(0.22486306, red.A, 1e-7);
        AssertEx.Near(0.12584630, red.B, 1e-7);
    }

    private static void OklabRoundTrip()
    {
        SrgbColor[] colors =
        [
            new(0, 0, 0),
            new(1, 1, 1),
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 1),
            new(0.15, 0.42, 0.83),
            new(0.91, 0.37, 0.12),
        ];
        foreach (var color in colors)
        {
            var roundTrip = ColorMath.OklabToSrgb(ColorMath.SrgbToOklab(color));
            AssertEx.Near(color.R, roundTrip.R, 2e-6);
            AssertEx.Near(color.G, roundTrip.G, 2e-6);
            AssertEx.Near(color.B, roundTrip.B, 2e-6);
        }
    }

    private static void HsvPrimaryColorsAndRoundTrip()
    {
        var red = ColorMath.SrgbToHsv(new SrgbColor(1, 0, 0));
        var green = ColorMath.SrgbToHsv(new SrgbColor(0, 1, 0));
        var blue = ColorMath.SrgbToHsv(new SrgbColor(0, 0, 1));
        AssertEx.Near(0, red.HueDegrees, 1e-10);
        AssertEx.Near(120, green.HueDegrees, 1e-10);
        AssertEx.Near(240, blue.HueDegrees, 1e-10);
        var color = new HsvColor(347, 0.63, 0.81);
        var roundTrip = ColorMath.SrgbToHsv(ColorMath.HsvToSrgb(color));
        AssertEx.Near(color.HueDegrees, roundTrip.HueDegrees, 1e-9);
        AssertEx.Near(color.Saturation, roundTrip.Saturation, 1e-9);
        AssertEx.Near(color.Value, roundTrip.Value, 1e-9);
    }

    private static void IdenticalColorScoresExactlyOneHundred()
    {
        var color = new SrgbColor(0.17, 0.52, 0.83);
        var result = ColorScoreV1.Score(color, color);
        AssertEx.Near(100, result.Similarity, 0);
        AssertEx.Near(0, result.DeltaEOK, 0);
        AssertEx.Near(0, result.DeltaLightness, 0);
        AssertEx.Near(0, result.DeltaChroma, 0);
    }

    private static void SimilarityMonotonicallyDecreases()
    {
        var target = new SrgbColor(0.5, 0.5, 0.5);
        var results = new[]
        {
            ColorScoreV1.Score(target, new SrgbColor(0.52, 0.52, 0.52)),
            ColorScoreV1.Score(target, new SrgbColor(0.65, 0.65, 0.65)),
            ColorScoreV1.Score(target, new SrgbColor(0.95, 0.95, 0.95)),
        };
        AssertEx.True(results[0].DeltaEOK < results[1].DeltaEOK, "较远灰色的 ΔE 未增大。" );
        AssertEx.True(results[1].DeltaEOK < results[2].DeltaEOK, "最远灰色的 ΔE 未增大。" );
        AssertEx.True(results[0].Similarity > results[1].Similarity, "相似度未单调下降。" );
        AssertEx.True(results[1].Similarity > results[2].Similarity, "相似度未单调下降。" );
    }

    private static void HueDifferenceWrapsCorrectly()
    {
        AssertEx.Near(2, ColorMath.ShortestHueDifference(1, 359), 1e-12);
        AssertEx.Near(-2, ColorMath.ShortestHueDifference(359, 1), 1e-12);
        AssertEx.Near(20, ColorMath.ShortestHueDifference(10, 350), 1e-12);
    }

    private static void GrayDoesNotReportHue()
    {
        var result = ColorScoreV1.Score(
            new SrgbColor(0.5, 0.5, 0.5),
            new SrgbColor(0.6, 0.6, 0.6));
        AssertEx.False(result.HueIsDefined, "灰色错误地报告了色相方向。" );
        AssertEx.True(result.DeltaHueDegrees is null, "灰色色相差应为空。" );
    }

    private static void NearBlackDoesNotReportSaturation()
    {
        var result = ColorScoreV1.Score(
            new SrgbColor(0, 0, 0),
            new SrgbColor(0.005, 0.002, 0.001));
        AssertEx.False(result.HsvSaturationIsDefined, "近黑颜色报告了不稳定 HSV 饱和度。" );
    }

    private static void HsvSaturationAndValueAreReported()
    {
        var result = ColorScoreV1.Score(
            new SrgbColor(1, 0, 0),
            new SrgbColor(1, 0.5, 0.5));
        AssertEx.Near(-50, result.DeltaHsvSaturation ?? double.NaN, 1e-10);
        AssertEx.Near(0, result.DeltaHsvValue, 1e-10);
        AssertEx.True(result.DeltaChroma < 0, "粉红相对红色应当感知彩度偏低。" );
    }

    private static void ColorReportIsFinite()
    {
        var result = ColorScoreV1.Score(
            new SrgbColor(0.12, 0.78, 0.42),
            new SrgbColor(0.81, 0.16, 0.63));
        AssertFinite(
            result.Similarity,
            result.DeltaEOK,
            result.DeltaLightness,
            result.DeltaChroma,
            result.DeltaA,
            result.DeltaB,
            result.DeltaHueDegrees ?? double.NaN,
            result.DeltaHsvSaturation ?? double.NaN,
            result.DeltaHsvValue);
    }
}
