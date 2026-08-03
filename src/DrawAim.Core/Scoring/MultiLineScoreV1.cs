using DrawAim.Core.Geometry;

namespace DrawAim.Core.Scoring;

public sealed record MultiLineScoreResult(
    double Total,
    double TargetCoverage,
    double UserPrecision,
    double ShapeF1,
    double LayoutSimilarity,
    double MissingGeometryPercent,
    double ExtraGeometryPercent,
    double PositionErrorNormalized,
    double LengthErrorRatio,
    double DirectionErrorDegrees,
    double CurvatureError)
{
    public const string ScoringVersion = "MultiLineScoreV1";
}

public static class MultiLineScoreV1
{
    public const string Version = "MultiLineScoreV1";

    public static MultiLineScoreResult Score(
        IReadOnlyList<TargetCurve> target,
        IReadOnlyList<LogicalStroke> answer,
        double toleranceNormalized = 0.02,
        int gridResolution = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(answer);
        if (!double.IsFinite(toleranceNormalized) ||
            toleranceNormalized is <= 0 or > 0.25)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceNormalized));
        }

        if (gridResolution is < 64 or > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(gridResolution));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var targetMask = new byte[checked(gridResolution * gridResolution)];
        var userMask = new byte[targetMask.Length];
        foreach (var curve in target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RasterizePolyline(
                curve.Polyline,
                targetMask,
                gridResolution,
                cancellationToken);
        }

        foreach (var stroke in answer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RasterizePolyline(
                stroke.Samples.Select(static sample => sample.Position).ToArray(),
                userMask,
                gridResolution,
                cancellationToken);
        }

        var targetCount = CountOccupied(targetMask, cancellationToken);
        var userCount = CountOccupied(userMask, cancellationToken);
        if (targetCount == 0)
        {
            return EmptyResult();
        }

        var targetField = DistanceTransform(targetMask, gridResolution, cancellationToken);
        var userField = DistanceTransform(userMask, gridResolution, cancellationToken);
        var toleranceCells = toleranceNormalized * (gridResolution - 1);
        var targetCoverage = AverageSoftCoverage(
            targetMask,
            userField,
            toleranceCells,
            cancellationToken);
        var userPrecision = userCount == 0
            ? 0
            : AverageSoftCoverage(
                userMask,
                targetField,
                toleranceCells,
                cancellationToken);
        var shapeF1 = targetCoverage + userPrecision <= GeometryMath.Epsilon
            ? 0
            : (2 * targetCoverage * userPrecision) / (targetCoverage + userPrecision);

        var truncate = 4 * toleranceCells;
        var activeCount = 0;
        var layoutDifference = 0.0;
        var extraCells = 0;
        for (var index = 0; index < targetMask.Length; index++)
        {
            if ((index & 16_383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var rawTargetDistance = targetField[index];
            var rawUserDistance = userField[index];
            var targetDistance = Math.Min(rawTargetDistance, truncate);
            var userDistance = Math.Min(rawUserDistance, truncate);
            if (Math.Min(rawTargetDistance, rawUserDistance) < truncate)
            {
                layoutDifference += Math.Abs(targetDistance - userDistance);
                activeCount++;
            }

            if (userMask[index] != 0 && targetField[index] > toleranceCells)
            {
                extraCells++;
            }
        }

        var layoutSimilarity = activeCount == 0 || truncate <= GeometryMath.Epsilon
            ? 0
            : 100 * Math.Clamp(
                1 - (layoutDifference / activeCount / truncate),
                0,
                1);
        var total = shapeF1 * (0.75 + (0.25 * layoutSimilarity / 100));
        var targetCentroid = Centroid(targetMask, gridResolution, cancellationToken);
        var userCentroid = Centroid(userMask, gridResolution, cancellationToken);
        var positionError = userCount == 0
            ? Math.Sqrt(2)
            : Point2.Distance(targetCentroid, userCentroid);
        var lengthError = targetCount == 0
            ? 0
            : (userCount - targetCount) / (double)targetCount;
        var directionError = DirectionError(
            targetMask,
            userMask,
            gridResolution,
            targetCount,
            userCount,
            cancellationToken);
        var curvatureError = Math.Abs(
            AverageTurningPerLength(
                target.Select(static curve => curve.Polyline),
                cancellationToken) -
            AverageTurningPerLength(answer.Select(stroke =>
                (IReadOnlyList<Point2>)stroke.Samples
                    .Select(static sample => sample.Position)
                    .ToArray()),
                cancellationToken));

        return new MultiLineScoreResult(
            ClampScore(total),
            ClampScore(targetCoverage),
            ClampScore(userPrecision),
            ClampScore(shapeF1),
            ClampScore(layoutSimilarity),
            ClampScore(100 - targetCoverage),
            userCount == 0 ? 0 : ClampScore(100 * extraCells / userCount),
            FiniteOrZero(positionError),
            FiniteOrZero(lengthError),
            FiniteOrZero(directionError),
            FiniteOrZero(curvatureError));
    }

    private static void RasterizePolyline(
        IReadOnlyList<Point2> points,
        byte[] mask,
        int resolution,
        CancellationToken cancellationToken)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mark(points[0], mask, resolution);
            return;
        }

        for (var index = 0; index < points.Count - 1; index++)
        {
            if ((index & 1_023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var start = points[index];
            var end = points[index + 1];
            if (!start.IsFinite || !end.IsFinite)
            {
                continue;
            }

            if (end.X < start.X || (end.X == start.X && end.Y < start.Y))
            {
                (start, end) = (end, start);
            }

            if (!TryClipToUnitSquare(start, end, out start, out end))
            {
                continue;
            }

            var delta = end - start;
            var steps = Math.Max(
                1,
                (int)Math.Ceiling(
                    Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) *
                    (resolution - 1) * 2));
            for (var step = 0; step <= steps; step++)
            {
                if ((step & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Mark(Point2.Lerp(start, end, step / (double)steps), mask, resolution);
            }
        }
    }

    private static bool TryClipToUnitSquare(
        Point2 start,
        Point2 end,
        out Point2 clippedStart,
        out Point2 clippedEnd)
    {
        clippedStart = default;
        clippedEnd = default;
        if (!start.IsFinite || !end.IsFinite)
        {
            return false;
        }

        // Coordinates this far outside a normalized drawing square cannot originate
        // from the supported input path. Ignoring them also avoids numerically
        // meaningless subtraction near Double.MaxValue while keeping scoring total.
        const double maximumSupportedMagnitude = 1_000_000_000_000;
        if (Math.Abs(start.X) > maximumSupportedMagnitude ||
            Math.Abs(start.Y) > maximumSupportedMagnitude ||
            Math.Abs(end.X) > maximumSupportedMagnitude ||
            Math.Abs(end.Y) > maximumSupportedMagnitude)
        {
            return false;
        }

        var delta = end - start;
        if (!delta.IsFinite)
        {
            return false;
        }

        var minimumT = 0.0;
        var maximumT = 1.0;
        if (!ClipTest(-delta.X, start.X, ref minimumT, ref maximumT) ||
            !ClipTest(delta.X, 1 - start.X, ref minimumT, ref maximumT) ||
            !ClipTest(-delta.Y, start.Y, ref minimumT, ref maximumT) ||
            !ClipTest(delta.Y, 1 - start.Y, ref minimumT, ref maximumT))
        {
            return false;
        }

        clippedStart = ClampToUnitSquare(Point2.Lerp(start, end, minimumT));
        clippedEnd = ClampToUnitSquare(Point2.Lerp(start, end, maximumT));
        return clippedStart.IsFinite && clippedEnd.IsFinite;
    }

    private static bool ClipTest(
        double direction,
        double distance,
        ref double minimumT,
        ref double maximumT)
    {
        if (Math.Abs(direction) <= GeometryMath.Epsilon)
        {
            return distance >= 0;
        }

        var ratio = distance / direction;
        if (direction < 0)
        {
            if (ratio > maximumT)
            {
                return false;
            }

            minimumT = Math.Max(minimumT, ratio);
        }
        else
        {
            if (ratio < minimumT)
            {
                return false;
            }

            maximumT = Math.Min(maximumT, ratio);
        }

        return minimumT <= maximumT;
    }

    private static Point2 ClampToUnitSquare(Point2 point) => new(
        Math.Clamp(point.X, 0, 1),
        Math.Clamp(point.Y, 0, 1));

    private static void Mark(Point2 point, byte[] mask, int resolution)
    {
        if (!point.IsFinite ||
            point.X < 0 || point.X > 1 ||
            point.Y < 0 || point.Y > 1)
        {
            return;
        }

        var x = Math.Clamp((int)Math.Round(point.X * (resolution - 1)), 0, resolution - 1);
        var y = Math.Clamp((int)Math.Round(point.Y * (resolution - 1)), 0, resolution - 1);
        mask[(y * resolution) + x] = 1;
    }

    private static float[] DistanceTransform(
        byte[] mask,
        int resolution,
        CancellationToken cancellationToken)
    {
        const float diagonal = 1.41421356F;
        var field = new float[mask.Length];
        for (var index = 0; index < mask.Length; index++)
        {
            field[index] = mask[index] == 0 ? float.PositiveInfinity : 0;
        }

        for (var y = 0; y < resolution; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < resolution; x++)
            {
                var index = (y * resolution) + x;
                var best = field[index];
                if (x > 0)
                {
                    best = Math.Min(best, field[index - 1] + 1);
                }

                if (y > 0)
                {
                    best = Math.Min(best, field[index - resolution] + 1);
                    if (x > 0)
                    {
                        best = Math.Min(best, field[index - resolution - 1] + diagonal);
                    }

                    if (x < resolution - 1)
                    {
                        best = Math.Min(best, field[index - resolution + 1] + diagonal);
                    }
                }

                field[index] = best;
            }
        }

        for (var y = resolution - 1; y >= 0; y--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = resolution - 1; x >= 0; x--)
            {
                var index = (y * resolution) + x;
                var best = field[index];
                if (x < resolution - 1)
                {
                    best = Math.Min(best, field[index + 1] + 1);
                }

                if (y < resolution - 1)
                {
                    best = Math.Min(best, field[index + resolution] + 1);
                    if (x > 0)
                    {
                        best = Math.Min(best, field[index + resolution - 1] + diagonal);
                    }

                    if (x < resolution - 1)
                    {
                        best = Math.Min(best, field[index + resolution + 1] + diagonal);
                    }
                }

                field[index] = best;
            }
        }

        return field;
    }

    private static double AverageSoftCoverage(
        byte[] sourceMask,
        float[] destinationDistance,
        double toleranceCells,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var sum = 0.0;
        for (var index = 0; index < sourceMask.Length; index++)
        {
            if ((index & 16_383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (sourceMask[index] == 0)
            {
                continue;
            }

            var normalized = destinationDistance[index] / toleranceCells;
            sum += Math.Exp(-0.50 * normalized * normalized);
            count++;
        }

        return count == 0 ? 0 : 100 * sum / count;
    }

    private static int CountOccupied(byte[] mask, CancellationToken cancellationToken)
    {
        var count = 0;
        for (var index = 0; index < mask.Length; index++)
        {
            if ((index & 16_383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            count += mask[index] == 0 ? 0 : 1;
        }

        return count;
    }

    private static Point2 Centroid(
        byte[] mask,
        int resolution,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var xSum = 0.0;
        var ySum = 0.0;
        for (var y = 0; y < resolution; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < resolution; x++)
            {
                if (mask[(y * resolution) + x] == 0)
                {
                    continue;
                }

                count++;
                xSum += x / (double)(resolution - 1);
                ySum += y / (double)(resolution - 1);
            }
        }

        return count == 0 ? Point2.Zero : new Point2(xSum / count, ySum / count);
    }

    private static double DirectionError(
        byte[] target,
        byte[] user,
        int resolution,
        int targetCount,
        int userCount,
        CancellationToken cancellationToken)
    {
        if (targetCount < 2 || userCount < 2)
        {
            return 90;
        }

        var targetAngle = PrincipalAxis(target, resolution, cancellationToken);
        var userAngle = PrincipalAxis(user, resolution, cancellationToken);
        var difference = Math.Abs(targetAngle - userAngle) * 180 / Math.PI;
        difference %= 180;
        return difference > 90 ? 180 - difference : difference;
    }

    private static double PrincipalAxis(
        byte[] mask,
        int resolution,
        CancellationToken cancellationToken)
    {
        var centroid = Centroid(mask, resolution, cancellationToken);
        var xx = 0.0;
        var yy = 0.0;
        var xy = 0.0;
        for (var y = 0; y < resolution; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < resolution; x++)
            {
                if (mask[(y * resolution) + x] == 0)
                {
                    continue;
                }

                var dx = (x / (double)(resolution - 1)) - centroid.X;
                var dy = (y / (double)(resolution - 1)) - centroid.Y;
                xx += dx * dx;
                yy += dy * dy;
                xy += dx * dy;
            }
        }

        return 0.5 * Math.Atan2(2 * xy, xx - yy);
    }

    private static double AverageTurningPerLength(
        IEnumerable<IReadOnlyList<Point2>> polylines,
        CancellationToken cancellationToken)
    {
        var totalTurning = 0.0;
        var totalLength = 0.0;
        foreach (var polyline in polylines)
        {
            Point2? previousEnd = null;
            Point2? previousDirection = null;
            for (var index = 0; index < polyline.Count - 1; index++)
            {
                if ((index & 1_023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!TryClipToUnitSquare(
                        polyline[index],
                        polyline[index + 1],
                        out var start,
                        out var end))
                {
                    previousEnd = null;
                    previousDirection = null;
                    continue;
                }

                var segment = end - start;
                var length = segment.Length;
                if (length <= GeometryMath.Epsilon)
                {
                    continue;
                }

                var direction = segment / length;
                if (previousEnd is { } precedingEnd &&
                    previousDirection is { } precedingDirection &&
                    Point2.Distance(precedingEnd, start) <= 1e-7)
                {
                    totalTurning += Math.Acos(Math.Clamp(
                        Point2.Dot(precedingDirection, direction),
                        -1,
                        1));
                }

                totalLength += length;
                previousEnd = end;
                previousDirection = direction;
            }
        }

        return totalLength <= GeometryMath.Epsilon ? 0 : totalTurning / totalLength;
    }

    private static MultiLineScoreResult EmptyResult() =>
        new(0, 0, 0, 0, 0, 100, 0, 0, 0, 90, 0);

    private static double ClampScore(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
