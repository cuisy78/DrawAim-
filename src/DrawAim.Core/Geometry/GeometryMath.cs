namespace DrawAim.Core.Geometry;

public readonly record struct NearestPointResult(
    double Distance,
    Point2 Point,
    int SegmentIndex,
    double SegmentFraction,
    double ArcPosition,
    Point2 Tangent);

public static class GeometryMath
{
    public const double Epsilon = 1e-9;

    public static double PolylineLength(IReadOnlyList<Point2> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var total = 0.0;
        for (var index = 1; index < points.Count; index++)
        {
            var distance = Point2.Distance(points[index - 1], points[index]);
            if (double.IsFinite(distance))
            {
                total += distance;
            }
        }

        return total;
    }

    public static Rect2 Bounds(IReadOnlyList<Point2> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return new Rect2(0, 0, 0, 0);
        }

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var point in points)
        {
            if (!point.IsFinite)
            {
                continue;
            }

            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return double.IsFinite(minX)
            ? new Rect2(minX, minY, maxX - minX, maxY - minY)
            : new Rect2(0, 0, 0, 0);
    }

    public static Rect2 BezierBounds(CubicBezier2 curve)
    {
        var parameters = new List<double> { 0, 1 };
        AddDerivativeRoots(curve.P0.X, curve.P1.X, curve.P2.X, curve.P3.X, parameters);
        AddDerivativeRoots(curve.P0.Y, curve.P1.Y, curve.P2.Y, curve.P3.Y, parameters);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var parameter in parameters)
        {
            var point = curve.Evaluate(parameter);
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    public static IReadOnlyList<Point2> FlattenBezier(
        CubicBezier2 bezier,
        double tolerance,
        int maximumDepth = 18)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        if (maximumDepth is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        var output = new List<Point2> { bezier.P0 };
        FlattenRecursive(bezier, tolerance * tolerance, maximumDepth, output);
        return output;
    }

    public static IReadOnlyList<Point2> ResampleByArcLength(
        IReadOnlyList<Point2> points,
        double spacing,
        int maximumSamples = 65_536)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(spacing) || spacing <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacing));
        }

        if (maximumSamples < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        }

        var clean = RemoveInvalidAndDuplicatePoints(points);
        if (clean.Count <= 1)
        {
            return clean;
        }

        var cumulative = BuildCumulativeLengths(clean);
        var length = cumulative[^1];
        if (length <= Epsilon)
        {
            return [clean[0]];
        }

        var count = Math.Clamp((int)Math.Ceiling(length / spacing) + 1, 2, maximumSamples);
        var result = new Point2[count];
        var segment = 1;
        for (var index = 0; index < count; index++)
        {
            var target = index == count - 1
                ? length
                : (length * index) / (count - 1);
            while (segment < cumulative.Length - 1 && cumulative[segment] < target)
            {
                segment++;
            }

            var startLength = cumulative[segment - 1];
            var segmentLength = cumulative[segment] - startLength;
            var fraction = segmentLength > Epsilon
                ? (target - startLength) / segmentLength
                : 0;
            result[index] = Point2.Lerp(clean[segment - 1], clean[segment], fraction);
        }

        return result;
    }

    public static NearestPointResult NearestPointOnPolyline(
        Point2 point,
        IReadOnlyList<Point2> polyline)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        if (polyline.Count == 0)
        {
            return new NearestPointResult(
                double.PositiveInfinity,
                Point2.Zero,
                -1,
                0,
                0,
                Point2.Zero);
        }

        if (polyline.Count == 1)
        {
            return new NearestPointResult(
                Point2.Distance(point, polyline[0]),
                polyline[0],
                0,
                0,
                0,
                Point2.Zero);
        }

        var totalLength = PolylineLength(polyline);
        var travelled = 0.0;
        var bestDistanceSquared = double.PositiveInfinity;
        var bestPoint = polyline[0];
        var bestSegment = 0;
        var bestFraction = 0.0;
        var bestArcPosition = 0.0;
        var bestTangent = Point2.Zero;

        for (var index = 0; index < polyline.Count - 1; index++)
        {
            var start = polyline[index];
            var end = polyline[index + 1];
            var vector = end - start;
            var lengthSquared = vector.LengthSquared;
            var segmentLength = Math.Sqrt(lengthSquared);
            if (lengthSquared <= Epsilon)
            {
                continue;
            }

            var fraction = Math.Clamp(Point2.Dot(point - start, vector) / lengthSquared, 0, 1);
            var candidate = start + (vector * fraction);
            var distanceSquared = (point - candidate).LengthSquared;
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestPoint = candidate;
                bestSegment = index;
                bestFraction = fraction;
                bestArcPosition = totalLength > Epsilon
                    ? (travelled + (segmentLength * fraction)) / totalLength
                    : 0;
                bestTangent = vector / segmentLength;
            }

            travelled += segmentLength;
        }

        if (!double.IsFinite(bestDistanceSquared))
        {
            return new NearestPointResult(
                Point2.Distance(point, polyline[0]),
                polyline[0],
                0,
                0,
                0,
                Point2.Zero);
        }

        return new NearestPointResult(
            Math.Sqrt(bestDistanceSquared),
            bestPoint,
            bestSegment,
            bestFraction,
            bestArcPosition,
            bestTangent);
    }

    public static double DistanceToPolyline(Point2 point, IReadOnlyList<Point2> polyline) =>
        NearestPointOnPolyline(point, polyline).Distance;

    public static double DistanceToSegment(Point2 point, Point2 start, Point2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared;
        if (lengthSquared <= Epsilon)
        {
            return Point2.Distance(point, start);
        }

        var fraction = Math.Clamp(Point2.Dot(point - start, segment) / lengthSquared, 0, 1);
        return Point2.Distance(point, start + (segment * fraction));
    }

    public static bool HasSelfIntersection(
        IReadOnlyList<Point2> polyline,
        double epsilon = 1e-8)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        for (var first = 0; first < polyline.Count - 1; first++)
        {
            for (var second = first + 2; second < polyline.Count - 1; second++)
            {
                if (first == 0 && second == polyline.Count - 2 &&
                    Point2.Distance(polyline[0], polyline[^1]) <= epsilon)
                {
                    continue;
                }

                if (SegmentsIntersect(
                    polyline[first],
                    polyline[first + 1],
                    polyline[second],
                    polyline[second + 1],
                    epsilon))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool PolylinesIntersect(
        IReadOnlyList<Point2> first,
        IReadOnlyList<Point2> second,
        double epsilon = 1e-8)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        for (var firstIndex = 0; firstIndex < first.Count - 1; firstIndex++)
        {
            for (var secondIndex = 0; secondIndex < second.Count - 1; secondIndex++)
            {
                if (SegmentsIntersect(
                    first[firstIndex],
                    first[firstIndex + 1],
                    second[secondIndex],
                    second[secondIndex + 1],
                    epsilon))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool SegmentsIntersect(
        Point2 firstStart,
        Point2 firstEnd,
        Point2 secondStart,
        Point2 secondEnd,
        double epsilon = 1e-8)
    {
        var firstVector = firstEnd - firstStart;
        var secondVector = secondEnd - secondStart;
        if (firstVector.LengthSquared <= Epsilon)
        {
            return DistanceToSegment(firstStart, secondStart, secondEnd) <= epsilon;
        }

        if (secondVector.LengthSquared <= Epsilon)
        {
            return DistanceToSegment(secondStart, firstStart, firstEnd) <= epsilon;
        }

        var cross = Point2.Cross(firstVector, secondVector);
        var offset = secondStart - firstStart;

        if (Math.Abs(cross) <= epsilon)
        {
            if (Math.Abs(Point2.Cross(offset, firstVector)) > epsilon)
            {
                return false;
            }

            var lengthSquared = firstVector.LengthSquared;
            if (lengthSquared <= Epsilon)
            {
                return Point2.Distance(firstStart, secondStart) <= epsilon;
            }

            var start = Point2.Dot(offset, firstVector) / lengthSquared;
            var end = start + (Point2.Dot(secondVector, firstVector) / lengthSquared);
            return Math.Max(Math.Min(start, end), 0) <= Math.Min(Math.Max(start, end), 1) + epsilon;
        }

        var t = Point2.Cross(offset, secondVector) / cross;
        var u = Point2.Cross(offset, firstVector) / cross;
        return t >= -epsilon && t <= 1 + epsilon && u >= -epsilon && u <= 1 + epsilon;
    }

    public static bool HasNonAdjacentNearOverlap(
        IReadOnlyList<Point2> polyline,
        double minimumDistance,
        int minimumIndexGap = 4)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        if (minimumDistance <= 0)
        {
            return false;
        }

        for (var index = 0; index < polyline.Count; index++)
        {
            for (var other = index + minimumIndexGap; other < polyline.Count; other++)
            {
                if (Point2.Distance(polyline[index], polyline[other]) < minimumDistance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static IReadOnlyList<Point2> RemoveInvalidAndDuplicatePoints(
        IReadOnlyList<Point2> points)
    {
        var clean = new List<Point2>(points.Count);
        foreach (var point in points)
        {
            if (!point.IsFinite)
            {
                continue;
            }

            if (clean.Count == 0 || Point2.Distance(clean[^1], point) > Epsilon)
            {
                clean.Add(point);
            }
        }

        return clean;
    }

    private static double[] BuildCumulativeLengths(IReadOnlyList<Point2> points)
    {
        var cumulative = new double[points.Count];
        for (var index = 1; index < points.Count; index++)
        {
            cumulative[index] = cumulative[index - 1] +
                                Point2.Distance(points[index - 1], points[index]);
        }

        return cumulative;
    }

    private static void AddDerivativeRoots(
        double p0,
        double p1,
        double p2,
        double p3,
        ICollection<double> output)
    {
        var a = -p0 + (3 * p1) - (3 * p2) + p3;
        var b = (3 * p0) - (6 * p1) + (3 * p2);
        var c = (-3 * p0) + (3 * p1);
        var quadratic = 3 * a;
        var linear = 2 * b;

        if (Math.Abs(quadratic) <= Epsilon)
        {
            if (Math.Abs(linear) <= Epsilon)
            {
                return;
            }

            AddRoot(-c / linear, output);
            return;
        }

        var discriminant = (linear * linear) - (4 * quadratic * c);
        if (discriminant < -Epsilon)
        {
            return;
        }

        discriminant = Math.Max(0, discriminant);
        var root = Math.Sqrt(discriminant);
        AddRoot((-linear - root) / (2 * quadratic), output);
        AddRoot((-linear + root) / (2 * quadratic), output);
    }

    private static void AddRoot(double value, ICollection<double> output)
    {
        if (value > 0 && value < 1 && double.IsFinite(value))
        {
            output.Add(value);
        }
    }

    private static void FlattenRecursive(
        CubicBezier2 curve,
        double toleranceSquared,
        int remainingDepth,
        List<Point2> output)
    {
        if (remainingDepth == 0 || IsFlatEnough(curve, toleranceSquared))
        {
            output.Add(curve.P3);
            return;
        }

        Split(curve, out var left, out var right);
        FlattenRecursive(left, toleranceSquared, remainingDepth - 1, output);
        FlattenRecursive(right, toleranceSquared, remainingDepth - 1, output);
    }

    private static bool IsFlatEnough(CubicBezier2 curve, double toleranceSquared)
    {
        var chord = curve.P3 - curve.P0;
        var chordLengthSquared = chord.LengthSquared;
        if (chordLengthSquared <= Epsilon)
        {
            return Math.Max(
                (curve.P1 - curve.P0).LengthSquared,
                (curve.P2 - curve.P0).LengthSquared) <= toleranceSquared;
        }

        var firstDistance = Point2.Cross(curve.P1 - curve.P0, chord);
        var secondDistance = Point2.Cross(curve.P2 - curve.P0, chord);
        return Math.Max(
            (firstDistance * firstDistance) / chordLengthSquared,
            (secondDistance * secondDistance) / chordLengthSquared) <= toleranceSquared;
    }

    private static void Split(
        CubicBezier2 curve,
        out CubicBezier2 left,
        out CubicBezier2 right)
    {
        var p01 = Point2.Lerp(curve.P0, curve.P1, 0.5);
        var p12 = Point2.Lerp(curve.P1, curve.P2, 0.5);
        var p23 = Point2.Lerp(curve.P2, curve.P3, 0.5);
        var p012 = Point2.Lerp(p01, p12, 0.5);
        var p123 = Point2.Lerp(p12, p23, 0.5);
        var midpoint = Point2.Lerp(p012, p123, 0.5);
        left = new CubicBezier2(curve.P0, p01, p012, midpoint);
        right = new CubicBezier2(midpoint, p123, p23, curve.P3);
    }
}
