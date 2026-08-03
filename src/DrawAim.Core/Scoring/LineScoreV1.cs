using DrawAim.Core.Geometry;

namespace DrawAim.Core.Scoring;

public sealed record LineScoreResult(
    double Total,
    double Accuracy,
    double Coverage,
    double Smoothness,
    double Economy,
    double MeanDistance,
    double P95Distance,
    double BacktrackingRatio,
    double ExcessLengthRatio)
{
    public const string ScoringVersion = "LineScoreV1";
}

public static class LineScoreV1
{
    public const string Version = "LineScoreV1";

    public static LineScoreResult Score(
        TargetCurve target,
        LogicalStroke answer,
        double toleranceRadiusDip)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(answer);
        if (!double.IsFinite(toleranceRadiusDip) || toleranceRadiusDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceRadiusDip));
        }

        var targetLength = target.Length;
        if (!double.IsFinite(targetLength) || targetLength <= GeometryMath.Epsilon)
        {
            return EmptyResult();
        }

        var answerPoints = answer.Samples
            .Select(static sample => sample.Position)
            .Where(static point => point.IsFinite)
            .ToArray();
        var answerLength = GeometryMath.PolylineLength(answerPoints);
        if (answerPoints.Length < 2 || answerLength <= GeometryMath.Epsilon)
        {
            return ScoreDegenerate(target, answerPoints, toleranceRadiusDip);
        }

        var spacing = Math.Clamp(
            Math.Min(toleranceRadiusDip / 3, targetLength / 384),
            0.20,
            2.0);
        var targetSamples = GeometryMath.ResampleByArcLength(target.Polyline, spacing);
        var userSamples = GeometryMath.ResampleByArcLength(answerPoints, spacing);
        if (targetSamples.Count == 0 || userSamples.Count < 2)
        {
            return EmptyResult();
        }

        var distances = new double[userSamples.Count];
        var nearest = new NearestPointResult[userSamples.Count];
        var distanceSum = 0.0;
        for (var index = 0; index < userSamples.Count; index++)
        {
            nearest[index] = GeometryMath.NearestPointOnPolyline(
                userSamples[index],
                target.Polyline);
            distances[index] = nearest[index].Distance;
            distanceSum += distances[index];
        }

        var meanDistance = distanceSum / distances.Length;
        var sortedDistances = (double[])distances.Clone();
        Array.Sort(sortedDistances);
        var p95Index = Math.Clamp(
            (int)Math.Ceiling(0.95 * sortedDistances.Length) - 1,
            0,
            sortedDistances.Length - 1);
        var p95Distance = sortedDistances[p95Index];
        var normalizedError =
            (0.70 * meanDistance / toleranceRadiusDip) +
            (0.30 * p95Distance / toleranceRadiusDip);
        var accuracy = 100 * Math.Exp(-0.85 * normalizedError);

        var coverageAccumulator = 0.0;
        foreach (var targetPoint in targetSamples)
        {
            var distance = GeometryMath.DistanceToPolyline(targetPoint, answerPoints);
            var normalized = distance / toleranceRadiusDip;
            coverageAccumulator += Math.Exp(-0.50 * normalized * normalized);
        }

        var coverage = 100 * coverageAccumulator / targetSamples.Count;
        var smoothness = ComputeSmoothness(
            userSamples,
            nearest,
            toleranceRadiusDip,
            spacing);
        var backtracking = ComputeBacktracking(nearest);
        var excessLength = Math.Max(0, (answerLength / targetLength) - 1.05);
        var economy = 100 * Math.Exp(
            (-1.80 * excessLength) -
            (3.00 * backtracking));

        accuracy = ClampScore(accuracy);
        coverage = ClampScore(coverage);
        smoothness = ClampScore(smoothness);
        economy = ClampScore(economy);
        var weighted =
            (0.50 * accuracy) +
            (0.30 * coverage) +
            (0.10 * smoothness) +
            (0.10 * economy);
        var coverageCap = 20 + (80 * Math.Pow(coverage / 100, 0.65));
        var total = ClampScore(Math.Min(weighted, coverageCap));

        return new LineScoreResult(
            total,
            accuracy,
            coverage,
            smoothness,
            economy,
            FiniteOrZero(meanDistance),
            FiniteOrZero(p95Distance),
            FiniteOrZero(backtracking),
            FiniteOrZero(excessLength));
    }

    private static double ComputeSmoothness(
        IReadOnlyList<Point2> userSamples,
        IReadOnlyList<NearestPointResult> nearest,
        double tolerance,
        double spacing)
    {
        if (userSamples.Count < 3)
        {
            return 0;
        }

        var tangentErrorSum = 0.0;
        var tangentCount = 0;
        var signedOffsets = new double[userSamples.Count];
        for (var index = 0; index < userSamples.Count; index++)
        {
            var previous = userSamples[Math.Max(0, index - 1)];
            var next = userSamples[Math.Min(userSamples.Count - 1, index + 1)];
            var userTangent = (next - previous).Normalized();
            var targetTangent = nearest[index].Tangent.Normalized();
            if (userTangent.LengthSquared > GeometryMath.Epsilon &&
                targetTangent.LengthSquared > GeometryMath.Epsilon)
            {
                var dot = Math.Clamp(
                    Math.Abs(Point2.Dot(userTangent, targetTangent)),
                    0,
                    1);
                tangentErrorSum += Math.Acos(dot) / (Math.PI / 2);
                tangentCount++;
            }

            var normal = new Point2(-targetTangent.Y, targetTangent.X);
            signedOffsets[index] = Point2.Dot(
                userSamples[index] - nearest[index].Point,
                normal);
        }

        var tangentError = tangentCount == 0
            ? 1
            : tangentErrorSum / tangentCount;
        var radius = Math.Clamp(
            (int)Math.Round((4 * tolerance) / Math.Max(spacing, 1e-6)),
            2,
            32);
        var residualSum = 0.0;
        for (var index = 0; index < signedOffsets.Length; index++)
        {
            var start = Math.Max(0, index - radius);
            var end = Math.Min(signedOffsets.Length - 1, index + radius);
            var localSum = 0.0;
            for (var other = start; other <= end; other++)
            {
                localSum += signedOffsets[other];
            }

            var localMean = localSum / (end - start + 1);
            residualSum += Math.Abs(signedOffsets[index] - localMean);
        }

        var jitterError = residualSum / signedOffsets.Length / tolerance;
        return 100 * Math.Exp(
            (-1.20 * tangentError) -
            (1.50 * jitterError));
    }

    private static double ComputeBacktracking(IReadOnlyList<NearestPointResult> nearest)
    {
        if (nearest.Count < 2)
        {
            return 1;
        }

        var forwardBacktrack = 0.0;
        var reverseBacktrack = 0.0;
        for (var index = 1; index < nearest.Count; index++)
        {
            var delta = nearest[index].ArcPosition - nearest[index - 1].ArcPosition;
            forwardBacktrack += Math.Max(0, -delta);
            reverseBacktrack += Math.Max(0, delta);
        }

        return Math.Min(forwardBacktrack, reverseBacktrack);
    }

    private static LineScoreResult ScoreDegenerate(
        TargetCurve target,
        IReadOnlyList<Point2> answer,
        double tolerance)
    {
        if (answer.Count == 0)
        {
            return EmptyResult();
        }

        var point = answer[0];
        var distance = GeometryMath.DistanceToPolyline(point, target.Polyline);
        var accuracy = ClampScore(100 * Math.Exp(-0.85 * distance / tolerance));
        var targetSamples = GeometryMath.ResampleByArcLength(
            target.Polyline,
            Math.Max(0.2, Math.Min(2, tolerance / 3)));
        var coverage = targetSamples.Count == 0
            ? 0
            : 100 * targetSamples.Average(targetPoint =>
            {
                var normalized = Point2.Distance(targetPoint, point) / tolerance;
                return Math.Exp(-0.50 * normalized * normalized);
            });
        var total = Math.Min(5, (0.50 * accuracy) + (0.30 * coverage));
        return new LineScoreResult(
            ClampScore(total),
            accuracy,
            ClampScore(coverage),
            0,
            0,
            FiniteOrZero(distance),
            FiniteOrZero(distance),
            1,
            0);
    }

    private static LineScoreResult EmptyResult() =>
        new(0, 0, 0, 0, 0, 0, 0, 1, 0);

    private static double ClampScore(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
