using DrawAim.Core.Geometry;
using DrawAim.Core.Randomness;

namespace DrawAim.Core.Generation;

public sealed class MultiLineGenerator
{
    public const string Version = "MultiLineGeneratorV2";

    public GenerationResult<MultiLineExercise> Generate(
        GenerationKey key,
        MultiLineGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = Validate(settings);
        if (validation is not null)
        {
            return GenerationResult<MultiLineExercise>.Failure(validation);
        }

        var seed = SeedDerivation.Derive(
            key,
            settings.MinimumLineCount,
            settings.MaximumLineCount,
            SeedDerivation.Quantize(settings.StraightWeight),
            SeedDerivation.Quantize(settings.CShapeWeight),
            SeedDerivation.Quantize(settings.SShapeWeight),
            SeedDerivation.Quantize(settings.MinimumLengthRatio),
            SeedDerivation.Quantize(settings.MaximumLengthRatio),
            SeedDerivation.Quantize(settings.MinimumCurvatureRatio),
            SeedDerivation.Quantize(settings.MaximumCurvatureRatio),
            settings.AllowIntersections ? 1 : 0,
            settings.Difficulty);
        var random = new Pcg32(seed);
        var lineCount = settings.MinimumLineCount == settings.MaximumLineCount
            ? settings.MinimumLineCount
            : settings.MinimumLineCount +
              random.NextInt32(settings.MaximumLineCount - settings.MinimumLineCount + 1);
        var lines = new List<TargetCurve>(lineCount);
        var lineGenerator = new TargetLineGenerator();
        var usedFallback = false;

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            TargetCurve? accepted = null;
            for (var attempt = 0; attempt < settings.MaximumAttemptsPerLine; attempt++)
            {
                var candidateKey = new GenerationKey(
                    Version,
                    ExerciseMode.CompositionCopy,
                    seed,
                    unchecked((key.ExerciseIndex * 4096) + (lineIndex * 256) + attempt),
                    key.SettingsFingerprint,
                    1,
                    1);
                var candidateSettings = new LineGenerationSettings
                {
                    StraightWeight = settings.StraightWeight,
                    CShapeWeight = settings.CShapeWeight,
                    SShapeWeight = settings.SShapeWeight,
                    MinimumLengthRatio = settings.MinimumLengthRatio,
                    MaximumLengthRatio = settings.MaximumLengthRatio,
                    MinimumCurvatureRatio = settings.MinimumCurvatureRatio,
                    MaximumCurvatureRatio = settings.MaximumCurvatureRatio,
                    SafeMarginRatio = settings.SafeMarginRatio,
                    Difficulty = settings.Difficulty,
                    MaximumAttempts = 4,
                };
                var generated = lineGenerator.Generate(candidateKey, candidateSettings);
                if (generated.IsSuccess &&
                    IsCompatible(generated.Value, lines, settings))
                {
                    accepted = generated.Value;
                    usedFallback |= generated.UsedFallback;
                    break;
                }
            }

            if (accepted is null)
            {
                accepted = CreateGridFallback(lineIndex, lineCount, ref random, settings);
                usedFallback = true;
            }

            if (accepted is null || !IsCompatible(accepted, lines, settings))
            {
                return GenerationResult<MultiLineExercise>.Failure(new GenerationError(
                    "CompositionGenerationFailed",
                    $"Could not place line {lineIndex + 1} without invalid overlap or crowding."));
            }

            lines.Add(accepted);
        }

        return GenerationResult<MultiLineExercise>.Success(
            new MultiLineExercise(lines),
            usedFallback);
    }

    private static GenerationError? Validate(MultiLineGenerationSettings settings)
    {
        if (settings.MinimumLineCount is < 1 or > 10 ||
            settings.MaximumLineCount is < 1 or > 10 ||
            settings.MaximumLineCount < settings.MinimumLineCount)
        {
            return new GenerationError("InvalidLineCount", "Line count must be between 1 and 10.");
        }

        var weightTotal = settings.StraightWeight +
                          settings.CShapeWeight +
                          settings.SShapeWeight;
        if (!AreFiniteAndNonNegative(
                settings.StraightWeight,
                settings.CShapeWeight,
                settings.SShapeWeight) ||
            !double.IsFinite(weightTotal) ||
            weightTotal <= 0)
        {
            return new GenerationError(
                "InvalidWeights",
                "Line weights must be finite, non-negative, and have a finite positive sum.");
        }

        if (!double.IsFinite(settings.MinimumLengthRatio) ||
            !double.IsFinite(settings.MaximumLengthRatio) ||
            settings.MinimumLengthRatio <= 0 ||
            settings.MaximumLengthRatio < settings.MinimumLengthRatio ||
            settings.MaximumLengthRatio > 0.9)
        {
            return new GenerationError("InvalidLength", "Composition length ratios are invalid.");
        }

        if (!double.IsFinite(settings.MinimumCurvatureRatio) ||
            !double.IsFinite(settings.MaximumCurvatureRatio) ||
            settings.MinimumCurvatureRatio < 0 ||
            settings.MaximumCurvatureRatio < settings.MinimumCurvatureRatio ||
            settings.MaximumCurvatureRatio > 0.6)
        {
            return new GenerationError("InvalidCurvature", "Composition curvature ratios are invalid.");
        }

        if (!double.IsFinite(settings.SafeMarginRatio) ||
            settings.SafeMarginRatio is < 0 or >= 0.25 ||
            !double.IsFinite(settings.MinimumSeparationRatio) ||
            settings.MinimumSeparationRatio is < 0 or > 0.2)
        {
            return new GenerationError("InvalidSpacing", "Composition margin or separation is invalid.");
        }

        if (settings.MaximumAttemptsPerLine is < 1 or > 4096)
        {
            return new GenerationError("InvalidAttempts", "Maximum attempts per line must be between 1 and 4096.");
        }

        return null;
    }

    internal static bool IsCompatible(
        TargetCurve candidate,
        IReadOnlyList<TargetCurve> accepted,
        MultiLineGenerationSettings settings)
    {
        const double minimumOverlapSeparation = 0.006;
        var spacing = Math.Max(settings.MinimumSeparationRatio, minimumOverlapSeparation);
        var candidateSamples = GeometryMath.ResampleByArcLength(
            candidate.Polyline,
            Math.Max(spacing / 2, 0.002));
        if (candidateSamples.Count == 0)
        {
            return false;
        }

        var closeToCombination = 0;
        foreach (var point in candidateSamples)
        {
            if (accepted.Any(existing =>
                GeometryMath.DistanceToPolyline(point, existing.Polyline) < spacing))
            {
                closeToCombination++;
            }
        }

        var maximumCombinationFraction = settings.AllowIntersections ? 0.40 : 0.20;
        if (closeToCombination / (double)candidateSamples.Count > maximumCombinationFraction)
        {
            return false;
        }

        foreach (var existing in accepted)
        {
            if (!settings.AllowIntersections &&
                GeometryMath.PolylinesIntersect(candidate.Polyline, existing.Polyline))
            {
                return false;
            }

            var existingSamples = GeometryMath.ResampleByArcLength(
                existing.Polyline,
                Math.Max(spacing / 2, 0.002));
            if (existingSamples.Count == 0)
            {
                return false;
            }

            var candidateCloseCount = candidateSamples.Count(point =>
                GeometryMath.DistanceToPolyline(point, existing.Polyline) < spacing);
            var existingCloseCount = existingSamples.Count(point =>
                GeometryMath.DistanceToPolyline(point, candidate.Polyline) < spacing);
            var maximumCloseFraction = settings.AllowIntersections ? 0.30 : 0.12;
            if (candidateCloseCount / (double)candidateSamples.Count > maximumCloseFraction ||
                existingCloseCount / (double)existingSamples.Count > maximumCloseFraction)
            {
                return false;
            }
        }

        if (settings.AllowIntersections &&
            !HasAcceptableIntersectionDensity(candidate, accepted, spacing))
        {
            return false;
        }

        return true;
    }

    private static bool HasAcceptableIntersectionDensity(
        TargetCurve candidate,
        IReadOnlyList<TargetCurve> accepted,
        double spacing)
    {
        if (accepted.Count == 0)
        {
            return true;
        }

        const int maximumIntersectionsPerPair = 2;
        var allIntersections = new List<Point2>();
        for (var first = 0; first < accepted.Count; first++)
        {
            for (var second = first + 1; second < accepted.Count; second++)
            {
                var pairIntersections = new List<Point2>();
                AddIntersectionPoints(
                    accepted[first].Polyline,
                    accepted[second].Polyline,
                    pairIntersections);
                allIntersections.AddRange(pairIntersections);
            }
        }

        foreach (var existing in accepted)
        {
            var pairIntersections = new List<Point2>();
            AddIntersectionPoints(candidate.Polyline, existing.Polyline, pairIntersections);
            if (pairIntersections.Count > maximumIntersectionsPerPair)
            {
                return false;
            }

            allIntersections.AddRange(pairIntersections);
        }

        // Two lines may cross naturally. A third pair crossing in the same small
        // neighborhood creates the dense star/black-knot combinations the mode
        // is intended to avoid.
        var clusterRadius = Math.Max(0.025, spacing * 1.5);
        foreach (var center in allIntersections)
        {
            var nearbyCount = allIntersections.Count(point =>
                Point2.Distance(center, point) <= clusterRadius);
            if (nearbyCount > 2)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIntersectionPoints(
        IReadOnlyList<Point2> first,
        IReadOnlyList<Point2> second,
        List<Point2> destination)
    {
        for (var firstIndex = 0; firstIndex < first.Count - 1; firstIndex++)
        {
            for (var secondIndex = 0; secondIndex < second.Count - 1; secondIndex++)
            {
                if (TryGetProperIntersection(
                        first[firstIndex],
                        first[firstIndex + 1],
                        second[secondIndex],
                        second[secondIndex + 1],
                        out var intersection))
                {
                    AddDistinct(destination, intersection, 1e-5);
                }
            }
        }
    }

    private static bool TryGetProperIntersection(
        Point2 firstStart,
        Point2 firstEnd,
        Point2 secondStart,
        Point2 secondEnd,
        out Point2 intersection)
    {
        intersection = default;
        var firstVector = firstEnd - firstStart;
        var secondVector = secondEnd - secondStart;
        var denominator = Point2.Cross(firstVector, secondVector);
        if (!double.IsFinite(denominator) || Math.Abs(denominator) <= 1e-10)
        {
            return false;
        }

        var offset = secondStart - firstStart;
        var firstFraction = Point2.Cross(offset, secondVector) / denominator;
        var secondFraction = Point2.Cross(offset, firstVector) / denominator;
        if (firstFraction is < -1e-8 or > 1.00000001 ||
            secondFraction is < -1e-8 or > 1.00000001)
        {
            return false;
        }

        intersection = firstStart + (firstVector * Math.Clamp(firstFraction, 0, 1));
        return intersection.IsFinite;
    }

    private static void AddDistinct(
        List<Point2> points,
        Point2 candidate,
        double minimumDistance)
    {
        if (points.All(point => Point2.Distance(point, candidate) > minimumDistance))
        {
            points.Add(candidate);
        }
    }

    private static TargetCurve? CreateGridFallback(
        int lineIndex,
        int lineCount,
        ref Pcg32 random,
        MultiLineGenerationSettings settings)
    {
        const int columns = 4;
        var rows = (int)Math.Ceiling(lineCount / (double)columns);
        var column = lineIndex % columns;
        var row = lineIndex / columns;
        var cellWidth = 1.0 / columns;
        var cellHeight = 1.0 / rows;
        var center = new Point2(
            (column + 0.5) * cellWidth,
            (row + 0.5) * cellHeight);
        var kind = TargetLineGenerator.SelectKind(
            ref random,
            settings.StraightWeight,
            settings.CShapeWeight,
            settings.SShapeWeight);
        var length = Math.Min(
            settings.MaximumLengthRatio,
            Math.Max(
                settings.MinimumLengthRatio,
                Math.Min(cellWidth, cellHeight) * 0.58));
        var curvature = kind == CurveKind.Straight
            ? 0
            : Math.Clamp(
                0.10,
                settings.MinimumCurvatureRatio,
                settings.MaximumCurvatureRatio);
        if ((lineIndex & 1) == 1)
        {
            curvature = -curvature;
        }

        var local = TargetLineGenerator.CreateLocalCurve(kind, length, curvature, 0.00025);
        var angle = ((lineIndex * 47) % 180) * Math.PI / 180;
        var transformed = local.Transform(angle, center);
        var target = new TargetCurve(kind, transformed, 0.00025, suggestedForward: true);
        var safe = new Rect2(
            settings.SafeMarginRatio,
            settings.SafeMarginRatio,
            1 - (2 * settings.SafeMarginRatio),
            1 - (2 * settings.SafeMarginRatio));
        return safe.Contains(new Point2(target.Bounds.Left, target.Bounds.Top)) &&
               safe.Contains(new Point2(target.Bounds.Right, target.Bounds.Bottom))
            ? target
            : null;
    }

    private static bool AreFiniteAndNonNegative(params double[] values) =>
        values.All(static value => double.IsFinite(value) && value >= 0);
}
