using DrawAim.Core.Geometry;
using DrawAim.Core.Scoring;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> LineScoreTests()
    {
        yield return new TestCase("LineScore/完美直线接近 100", PerfectStraightScoresNearOneHundred);
        yield return new TestCase("LineScore/完美曲线接近 100", PerfectCurveScoresNearOneHundred);
        yield return new TestCase("LineScore/正反方向不变", ReversingDirectionDoesNotChangeScore);
        yield return new TestCase("LineScore/采样密度不敏感", SamplingDensityDoesNotChangeScore);
        yield return new TestCase("LineScore/半程覆盖限制总分", HalfTraceIsCoverageLimited);
        yield return new TestCase("LineScore/整体偏移单调扣分", OffsetMonotonicallyReducesAccuracy);
        yield return new TestCase("LineScore/来回涂抹受到惩罚", RepeatedTracingIsPenalized);
        yield return new TestCase("LineScore/单点有限低分", SinglePointProducesFiniteLowScore);
        yield return new TestCase("LineScore/压力不影响评分", PressureDoesNotAffectLineScore);
    }

    private static void PerfectStraightScoresNearOneHundred()
    {
        var target = StraightTarget(new Point2(100, 200), new Point2(600, 200), 0.01);
        var result = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 8), 10);
        AssertEx.InRange(result.Total, 99, 100);
        AssertEx.InRange(result.Accuracy, 99.9, 100);
        AssertEx.InRange(result.Coverage, 99.9, 100);
        AssertEx.InRange(result.Smoothness, 99.9, 100);
        AssertEx.InRange(result.Economy, 99.9, 100);
    }

    private static void PerfectCurveScoresNearOneHundred()
    {
        var target = CurvedTarget();
        var result = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 2), 10);
        AssertEx.InRange(result.Total, 99, 100, $"完美曲线总分仅 {result.Total:F3}。" );
        AssertEx.InRange(result.Accuracy, 99.8, 100);
        AssertEx.InRange(result.Coverage, 99.8, 100);
    }

    private static void ReversingDirectionDoesNotChangeScore()
    {
        var target = CurvedTarget();
        var forward = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 3), 10);
        var reverse = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 3, reverse: true), 10);
        AssertEx.Near(forward.Total, reverse.Total, 0.5);
        AssertEx.Near(forward.Smoothness, reverse.Smoothness, 0.5);
        AssertEx.Near(forward.Economy, reverse.Economy, 0.5);
    }

    private static void SamplingDensityDoesNotChangeScore()
    {
        var target = CurvedTarget();
        var sparse = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 1), 10);
        var dense = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 8), 10);
        AssertEx.Near(sparse.Total, dense.Total, 2);
        AssertEx.Near(sparse.Accuracy, dense.Accuracy, 1);
        AssertEx.Near(sparse.Coverage, dense.Coverage, 1);
    }

    private static void HalfTraceIsCoverageLimited()
    {
        var target = StraightTarget(new Point2(100, 200), new Point2(700, 200), 0.01);
        var samples = GeometryMath.ResampleByArcLength(target.Polyline, 2).ToArray();
        var half = StrokeFromPoints(samples.Take((samples.Length / 2) + 1));
        var result = LineScoreV1.Score(target, half, 10);
        AssertEx.InRange(result.Coverage, 45, 58, $"半程 Coverage 为 {result.Coverage:F2}。" );
        AssertEx.InRange(result.Total, 35, 75, $"半程总分为 {result.Total:F2}。" );
        AssertEx.True(result.Total < result.Accuracy, "半程答案没有受到覆盖率上限限制。" );
    }

    private static void OffsetMonotonicallyReducesAccuracy()
    {
        var target = CurvedTarget();
        var accuracies = new[] { 0.0, 5.0, 10.0, 20.0, 40.0 }
            .Select(offset => LineScoreV1.Score(
                target,
                StrokeFromPoints(Offset(target.Polyline, new Point2(0, offset)), 2),
                10).Accuracy)
            .ToArray();
        for (var index = 1; index < accuracies.Length; index++)
        {
            AssertEx.True(
                accuracies[index] < accuracies[index - 1],
                $"偏移增加后 Accuracy 未下降：{string.Join(", ", accuracies.Select(value => value.ToString("F2")))}" );
        }
    }

    private static void RepeatedTracingIsPenalized()
    {
        var target = CurvedTarget();
        var once = LineScoreV1.Score(target, StrokeFromPoints(target.Polyline, 2), 10);
        var points = target.Polyline.ToList();
        points.AddRange(target.Polyline.Reverse());
        points.AddRange(target.Polyline);
        var repeated = LineScoreV1.Score(target, StrokeFromPoints(points, 2), 10);
        AssertEx.True(repeated.Economy < 20, $"三次来回描线的 Economy 仍为 {repeated.Economy:F2}。" );
        AssertEx.True(repeated.Total < once.Total - 8, "来回涂抹没有明显降低总分。" );
    }

    private static void SinglePointProducesFiniteLowScore()
    {
        var target = CurvedTarget();
        var result = LineScoreV1.Score(
            target,
            LogicalStroke.FromPoints([target.Bezier.P0]),
            10);
        AssertFinite(
            result.Total,
            result.Accuracy,
            result.Coverage,
            result.Smoothness,
            result.Economy);
        AssertEx.InRange(result.Total, 0, 5);
    }

    private static void PressureDoesNotAffectLineScore()
    {
        var target = CurvedTarget();
        var lowPressure = StrokeFromPoints(target.Polyline, 3, pressure: 0.05);
        var highPressure = StrokeFromPoints(target.Polyline, 3, pressure: 1);
        AssertEx.Equal(
            LineScoreV1.Score(target, lowPressure, 10),
            LineScoreV1.Score(target, highPressure, 10));
    }
}
