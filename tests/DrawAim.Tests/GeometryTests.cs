using DrawAim.Core.Geometry;

namespace DrawAim.Tests;

internal static partial class AllTests
{
    private static partial IEnumerable<TestCase> GeometryTests()
    {
        yield return new TestCase("Geometry/Point2 向量运算", PointVectorOperations);
        yield return new TestCase("Geometry/折线弧长", PolylineLengthIsCorrect);
        yield return new TestCase("Geometry/弧长重采样保留端点", ResamplingPreservesEndpoints);
        yield return new TestCase("Geometry/点到线段与折线距离", SegmentAndPolylineDistance);
        yield return new TestCase("Geometry/线段相交含退化点", SegmentIntersectionHandlesDegeneratePoints);
        yield return new TestCase("Geometry/自交检测", SelfIntersectionIsDetected);
        yield return new TestCase("Geometry/三次贝塞尔解析边界", BezierBoundsIncludeExtrema);
        yield return new TestCase("Geometry/LogicalStroke 清理非法输入", LogicalStrokeSanitizesInput);
    }

    private static void PointVectorOperations()
    {
        var first = new Point2(3, 4);
        var second = new Point2(-2, 5);
        AssertEx.Near(5, first.Length, 1e-12);
        AssertEx.Equal(new Point2(1, 9), first + second);
        AssertEx.Near(14, Point2.Dot(first, second), 1e-12);
        AssertEx.Near(23, Point2.Cross(first, second), 1e-12);
        AssertEx.Near(Math.Sqrt(26), Point2.Distance(first, second), 1e-12);
    }

    private static void PolylineLengthIsCorrect()
    {
        Point2[] points = [new(0, 0), new(3, 4), new(6, 8)];
        AssertEx.Near(10, GeometryMath.PolylineLength(points), 1e-12);
    }

    private static void ResamplingPreservesEndpoints()
    {
        Point2[] source = [new(0, 0), new(10, 0), new(10, 10)];
        var samples = GeometryMath.ResampleByArcLength(source, 2.5);
        AssertEx.Equal(source[0], samples[0]);
        AssertEx.Equal(source[^1], samples[^1]);
        AssertEx.Equal(9, samples.Count);
        for (var index = 1; index < samples.Count; index++)
        {
            AssertEx.Near(2.5, Point2.Distance(samples[index - 1], samples[index]), 0.75);
        }
    }

    private static void SegmentAndPolylineDistance()
    {
        AssertEx.Near(
            3,
            GeometryMath.DistanceToSegment(new Point2(5, 3), new Point2(0, 0), new Point2(10, 0)),
            1e-12);
        Point2[] polyline = [new(0, 0), new(10, 0), new(10, 10)];
        var nearest = GeometryMath.NearestPointOnPolyline(new Point2(12, 7), polyline);
        AssertEx.Near(2, nearest.Distance, 1e-12);
        AssertEx.Equal(1, nearest.SegmentIndex);
        AssertEx.Near(0.85, nearest.ArcPosition, 1e-12);
    }

    private static void SegmentIntersectionHandlesDegeneratePoints()
    {
        AssertEx.True(
            GeometryMath.SegmentsIntersect(
                new Point2(0, 0), new Point2(10, 10),
                new Point2(0, 10), new Point2(10, 0)),
            "交叉线段未识别。" );
        AssertEx.True(
            GeometryMath.SegmentsIntersect(
                new Point2(5, 5), new Point2(5, 5),
                new Point2(0, 5), new Point2(10, 5)),
            "位于线段上的退化点未识别。" );
        AssertEx.False(
            GeometryMath.SegmentsIntersect(
                new Point2(5, 6), new Point2(5, 6),
                new Point2(0, 5), new Point2(10, 5)),
            "不在线段上的退化点被误判。" );
    }

    private static void SelfIntersectionIsDetected()
    {
        Point2[] bow = [new(0, 0), new(10, 10), new(0, 10), new(10, 0)];
        Point2[] simple = [new(0, 0), new(5, 3), new(10, 0)];
        AssertEx.True(GeometryMath.HasSelfIntersection(bow), "蝴蝶结折线未识别为自交。" );
        AssertEx.False(GeometryMath.HasSelfIntersection(simple), "普通折线被误判为自交。" );
    }

    private static void BezierBoundsIncludeExtrema()
    {
        var curve = new CubicBezier2(
            new Point2(0, 0),
            new Point2(0, 10),
            new Point2(10, 10),
            new Point2(10, 0));
        var bounds = GeometryMath.BezierBounds(curve);
        AssertEx.Near(0, bounds.Left, 1e-12);
        AssertEx.Near(10, bounds.Right, 1e-12);
        AssertEx.Near(7.5, bounds.Bottom, 1e-10);
        var flattened = GeometryMath.FlattenBezier(curve, 0.01);
        AssertEx.False(GeometryMath.HasSelfIntersection(flattened), "普通 C 曲线不应自交。" );
    }

    private static void LogicalStrokeSanitizesInput()
    {
        StrokeSample[] samples =
        [
            new(new Point2(1, 1), 1, -2),
            new(new Point2(double.NaN, 2), 2, 0.5),
            new(new Point2(1, 1), 3, 0.5),
            new(new Point2(2, 2), 0, 4),
        ];
        var stroke = new LogicalStroke(samples);
        AssertEx.Equal(2, stroke.Count);
        AssertEx.Near(0, stroke.Samples[0].Pressure, 0);
        AssertEx.Near(1, stroke.Samples[1].Pressure, 0);
        AssertEx.True(
            stroke.Samples[1].TimestampSeconds > stroke.Samples[0].TimestampSeconds,
            "时间戳没有恢复为单调递增。" );
    }
}
