using DrawAim.Core.Geometry;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static TargetCurve StraightTarget(
        Point2 start,
        Point2 end,
        double tolerance = 0.001)
    {
        var delta = end - start;
        return new TargetCurve(
            CurveKind.Straight,
            new CubicBezier2(
                start,
                start + (delta / 3),
                start + ((2 * delta) / 3),
                end),
            tolerance);
    }

    private static TargetCurve CurvedTarget(
        double scale = 400,
        Point2? origin = null,
        double tolerance = 0.05)
    {
        var offset = origin ?? new Point2(100, 200);
        return new TargetCurve(
            CurveKind.CShape,
            new CubicBezier2(
                offset,
                offset + new Point2(scale * 0.25, -scale * 0.35),
                offset + new Point2(scale * 0.75, -scale * 0.35),
                offset + new Point2(scale, 0)),
            tolerance);
    }

    private static LogicalStroke StrokeFromPoints(
        IEnumerable<Point2> points,
        int repeatsPerSegment = 1,
        bool reverse = false,
        double pressure = 1)
    {
        var source = points.ToArray();
        if (reverse)
        {
            Array.Reverse(source);
        }

        var expanded = new List<StrokeSample>();
        var timestamp = 0.0;
        if (source.Length > 0)
        {
            expanded.Add(new StrokeSample(source[0], timestamp, pressure));
        }

        for (var index = 1; index < source.Length; index++)
        {
            for (var step = 1; step <= repeatsPerSegment; step++)
            {
                timestamp += 1.0 / (120 * repeatsPerSegment);
                expanded.Add(new StrokeSample(
                    Point2.Lerp(source[index - 1], source[index], step / (double)repeatsPerSegment),
                    timestamp,
                    pressure));
            }
        }

        return new LogicalStroke(expanded);
    }

    private static IReadOnlyList<Point2> Offset(
        IEnumerable<Point2> points,
        Point2 offset) =>
        points.Select(point => point + offset).ToArray();

    private static void AssertFinite(params double[] values)
    {
        AssertEx.True(values.All(double.IsFinite), "结果包含 NaN 或 Infinity。");
    }
}
