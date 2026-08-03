using DrawAim.Core.Geometry;
using DrawAim.Core.Input;
using DrawAim.Core.Scoring;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> StabilizerTests()
    {
        yield return new TestCase("Stabilizer/0 完全旁路", StabilizerZeroIsIdentity);
        yield return new TestCase("Stabilizer/首点等于原始首点", StabilizerPreservesFirstPoint);
        yield return new TestCase("Stabilizer/maxLag 从不越界", StabilizerRespectsMaximumLag);
        yield return new TestCase("Stabilizer/相同输入确定性", StabilizerIsDeterministic);
        yield return new TestCase("Stabilizer/压力不改变中心轨迹", PressureDoesNotChangeStabilizedGeometry);
        yield return new TestCase("Stabilizer/强度增加高频抖动下降", StabilizerStrengthReducesJitter);
        yield return new TestCase("Stabilizer/采样率一致性", StabilizerIsApproximatelySampleRateInvariant);
        yield return new TestCase("Stabilizer/跨采样率评分差不超过 1", StabilizedScoreIsSampleRateInvariant);
        yield return new TestCase("Stabilizer/非法点与倒退时间安全处理", StabilizerHandlesInvalidSamples);
    }

    private static void StabilizerZeroIsIdentity()
    {
        var input = ParametricSamples(120, 1);
        var result = StrokeStabilizerV1.Stabilize(input, 0);
        AssertEx.Equal(input.Count, result.Count);
        for (var index = 0; index < input.Count; index++)
        {
            AssertEx.Equal(input[index].Position, result.Samples[index].Position);
        }
    }

    private static void StabilizerPreservesFirstPoint()
    {
        var stabilizer = new StrokeStabilizerV1(100);
        var first = new StrokeSample(new Point2(42, 73), 0.1, 0.6);
        AssertEx.Equal(first.Position, stabilizer.Process(first).Position);
    }

    private static void StabilizerRespectsMaximumLag()
    {
        foreach (var level in new[] { 1, 25, 50, 75, 100 })
        {
            var stabilizer = new StrokeStabilizerV1(level);
            _ = stabilizer.Process(new StrokeSample(new Point2(0, 0), 0, 1));
            for (var index = 1; index <= 200; index++)
            {
                var raw = new StrokeSample(new Point2(index * 20, index % 3), index / 240.0, 1);
                var stable = stabilizer.Process(raw);
                AssertEx.True(
                    Point2.Distance(raw.Position, stable.Position) <= stabilizer.MaxLagDip + 1e-8,
                    $"稳定 {level} 在第 {index} 点超过 maxLag。" );
            }
        }
    }

    private static void StabilizerIsDeterministic()
    {
        var input = ParametricSamples(240, 1.25);
        var first = StrokeStabilizerV1.Stabilize(input, 73);
        var second = StrokeStabilizerV1.Stabilize(input, 73);
        AssertEx.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            AssertEx.Equal(first.Samples[index], second.Samples[index]);
        }
    }

    private static void PressureDoesNotChangeStabilizedGeometry()
    {
        var firstInput = ParametricSamples(120, 1)
            .Select(sample => sample with { Pressure = 0.1 })
            .ToArray();
        var secondInput = ParametricSamples(120, 1)
            .Select((sample, index) => sample with { Pressure = (index % 10) / 9.0 })
            .ToArray();
        var first = StrokeStabilizerV1.Stabilize(firstInput, 80);
        var second = StrokeStabilizerV1.Stabilize(secondInput, 80);
        for (var index = 0; index < first.Count; index++)
        {
            AssertEx.Equal(first.Samples[index].Position, second.Samples[index].Position);
        }
    }

    private static void StabilizerStrengthReducesJitter()
    {
        var samples = new List<StrokeSample>();
        for (var index = 0; index < 600; index++)
        {
            var t = index / 240.0;
            samples.Add(new StrokeSample(
                new Point2(index * 0.5, 2 * Math.Sin(index * Math.PI * 0.72)),
                t,
                1));
        }

        var residuals = new List<double>();
        foreach (var level in new[] { 0, 25, 50, 75, 100 })
        {
            var stroke = StrokeStabilizerV1.Stabilize(samples, level);
            residuals.Add(SecondDifferenceRms(stroke.Positions));
        }

        for (var index = 1; index < residuals.Count; index++)
        {
            AssertEx.True(
                residuals[index] <= residuals[index - 1] + 1e-9,
                $"稳定强度增加后抖动未下降：{string.Join(", ", residuals.Select(value => value.ToString("F4")))}" );
        }

        AssertEx.True(
            residuals[^1] <= residuals[0] * 0.40,
            $"稳定 100 的高频残差没有至少降低 60%：{residuals[0]:F4} -> {residuals[^1]:F4}。" );
    }

    private static void StabilizerIsApproximatelySampleRateInvariant()
    {
        var reference = StabilizedPolyline(60, 75);
        foreach (var rate in new[] { 120, 240, 480 })
        {
            var candidate = StabilizedPolyline(rate, 75);
            var distance = SymmetricMeanDistance(reference, candidate);
            AssertEx.InRange(distance, 0, 1.0, $"60 Hz 与 {rate} Hz 的稳定结果相差 {distance:F3} DIP。" );
        }
    }

    private static void StabilizedScoreIsSampleRateInvariant()
    {
        var target = CurvedTarget(scale: 400, origin: new Point2(100, 260));
        var scores = new List<double>();
        foreach (var rate in new[] { 60, 120, 240, 480 })
        {
            var samples = new StrokeSample[rate + 1];
            for (var index = 0; index < samples.Length; index++)
            {
                var t = index / (double)rate;
                var raw = target.Bezier.Evaluate(t);
                var tangent = target.Bezier.Derivative(t).Normalized();
                var normal = new Point2(-tangent.Y, tangent.X);
                var jitter = 0.8 * Math.Sin(2 * Math.PI * 17 * t);
                samples[index] = new StrokeSample(raw + (normal * jitter), t, 1);
            }

            var stabilized = StrokeStabilizerV1.Stabilize(samples, 75);
            scores.Add(LineScoreV1.Score(target, stabilized, 8).Total);
        }

        var spread = scores.Max() - scores.Min();
        AssertEx.InRange(
            spread,
            0,
            1,
            $"60/120/240/480 Hz 稳定后评分相差 {spread:F3}：{string.Join(", ", scores.Select(score => score.ToString("F3")))}" );
    }

    private static void StabilizerHandlesInvalidSamples()
    {
        var stabilizer = new StrokeStabilizerV1(50);
        AssertEx.False(
            stabilizer.TryProcess(
                new StrokeSample(new Point2(double.NaN, 0), double.NaN, double.NaN),
                out _),
            "非法首点不应生成轨迹。" );
        var first = stabilizer.Process(new StrokeSample(new Point2(0, 0), 10, 1));
        AssertEx.False(
            stabilizer.TryProcess(
                new StrokeSample(new Point2(double.PositiveInfinity, 0), 5, 4),
                out _),
            "非法中间点应被丢弃，而不是生成重复轨迹。" );
        AssertEx.False(
            stabilizer.TryProcess(
                new StrokeSample(new Point2(1e200, -1e200), 11, 1),
                out _),
            "异常大坐标应被丢弃。" );
        var backwards = stabilizer.Process(new StrokeSample(new Point2(1, 0), 2, 1));
        AssertEx.True(backwards.TimestampSeconds > first.TimestampSeconds, "倒退时间没有恢复。" );
        AssertEx.True(backwards.Position.IsFinite, "非法样本污染了滤波器状态。" );
    }

    private static IReadOnlyList<StrokeSample> ParametricSamples(int rate, double duration)
    {
        var count = (int)Math.Round(rate * duration) + 1;
        var result = new StrokeSample[count];
        for (var index = 0; index < count; index++)
        {
            var t = index / (double)rate;
            var normalized = t / duration;
            result[index] = new StrokeSample(
                new Point2(
                    180 * normalized,
                    30 * Math.Sin(normalized * Math.PI * 2)),
                t,
                1);
        }

        return result;
    }

    private static IReadOnlyList<Point2> StabilizedPolyline(int rate, int level)
    {
        var stroke = StrokeStabilizerV1.Stabilize(ParametricSamples(rate, 1), level);
        return GeometryMath.ResampleByArcLength(stroke.Positions, 1);
    }

    private static double SecondDifferenceRms(IReadOnlyList<Point2> points)
    {
        var sum = 0.0;
        var count = 0;
        for (var index = 1; index < points.Count - 1; index++)
        {
            var residual = points[index - 1].Y - (2 * points[index].Y) + points[index + 1].Y;
            sum += residual * residual;
            count++;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private static double SymmetricMeanDistance(
        IReadOnlyList<Point2> first,
        IReadOnlyList<Point2> second)
    {
        var forward = first.Average(point => GeometryMath.DistanceToPolyline(point, second));
        var reverse = second.Average(point => GeometryMath.DistanceToPolyline(point, first));
        return (forward + reverse) / 2;
    }
}
