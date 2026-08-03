using DrawAim.Core.Geometry;
using DrawAim.Core.Randomness;

namespace DrawAim.Core.Generation;

public sealed class TargetLineGenerator
{
    public const string Version = "LineGeneratorV2";

    public GenerationResult<TargetCurve> Generate(
        GenerationKey key,
        LineGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = Validate(key, settings);
        if (validation is not null)
        {
            return GenerationResult<TargetCurve>.Failure(validation);
        }

        long[] seedValues =
        [
            SeedDerivation.Quantize(settings.StraightWeight),
            SeedDerivation.Quantize(settings.CShapeWeight),
            SeedDerivation.Quantize(settings.SShapeWeight),
            SeedDerivation.Quantize(settings.MinimumLengthRatio),
            SeedDerivation.Quantize(settings.MaximumLengthRatio),
            SeedDerivation.Quantize(settings.MinimumCurvatureRatio),
            SeedDerivation.Quantize(settings.MaximumCurvatureRatio),
            SeedDerivation.Quantize(settings.MinimumDirectionDegrees),
            SeedDerivation.Quantize(settings.MaximumDirectionDegrees),
            settings.Difficulty,
        ];
        var seed = SeedDerivation.Derive(key, seedValues);
        var diversitySeed = SeedDerivation.Derive(
            key with { ExerciseIndex = 0 },
            seedValues);
        var random = new Pcg32(seed);
        var exerciseLane = unchecked((uint)key.ExerciseIndex);
        var lengthPhase = (uint)((diversitySeed >> 32) & 7);
        var curvaturePhase = (uint)((diversitySeed >> 40) & 7);
        var curvatureSignPhase = (uint)((diversitySeed >> 48) & 1);
        var kind = SelectKind(
            ref random,
            settings.StraightWeight,
            settings.CShapeWeight,
            settings.SShapeWeight);
        var shortSide = Math.Min(key.CanvasWidthDip, key.CanvasHeightDip);
        var margin = shortSide * settings.SafeMarginRatio;
        var flatteningTolerance = Math.Clamp(shortSide / 4096.0, 0.0001, 0.25);

        for (var attempt = 0; attempt < settings.MaximumAttempts; attempt++)
        {
            // Eight equal strata retain a uniform marginal distribution, while
            // the coprime stride keeps adjacent exercises out of neighboring
            // length and curvature bands. Randomness remains inside each band.
            // Advancing the length phase once per eight-question block makes
            // length x curvature cover all 64 coarse combinations every 64
            // exercises instead of repeating only eight correlated pairs.
            var lengthBin = (lengthPhase +
                             ((exerciseLane & 7U) * 3U) +
                             ((exerciseLane >> 3) & 7U)) & 7U;
            var lengthFraction = (lengthBin + random.NextDouble()) / 8.0;
            var desiredLengthRatio = settings.MinimumLengthRatio +
                                     ((settings.MaximumLengthRatio -
                                       settings.MinimumLengthRatio) * lengthFraction);
            var desiredLength = shortSide * desiredLengthRatio;
            var curvature = 0.0;
            if (kind != CurveKind.Straight)
            {
                var curvatureBin = (curvaturePhase + (exerciseLane * 5U)) & 7U;
                var curvatureFraction = (curvatureBin + random.NextDouble()) / 8.0;
                var magnitude = settings.MinimumCurvatureRatio +
                                ((settings.MaximumCurvatureRatio -
                                  settings.MinimumCurvatureRatio) * curvatureFraction);
                var positive = ((exerciseLane + curvatureSignPhase) & 1U) == 0;
                curvature = positive ? magnitude : -magnitude;
            }

            var local = CreateLocalCurve(kind, desiredLength, curvature, flatteningTolerance);
            // Multiplication by five permutes all 16 direction strata while
            // keeping adjacent exercises at least two visual angle bins apart,
            // even after treating a line as undirected (modulo 180 degrees).
            var directionBin = (int)((exerciseLane * 5U) & 15U);
            var directionFraction = (directionBin + random.NextDouble()) / 16.0;
            var angleDegrees = settings.MinimumDirectionDegrees +
                               ((settings.MaximumDirectionDegrees - settings.MinimumDirectionDegrees) *
                                directionFraction);
            var rotated = local.Transform(angleDegrees * Math.PI / 180, Point2.Zero);
            var bounds = GeometryMath.BezierBounds(rotated);
            var minimumX = margin - bounds.Left;
            var maximumX = key.CanvasWidthDip - margin - bounds.Right;
            var minimumY = margin - bounds.Top;
            var maximumY = key.CanvasHeightDip - margin - bounds.Bottom;
            if (maximumX < minimumX || maximumY < minimumY)
            {
                continue;
            }

            var cell = (int)((exerciseLane * 7U) & 15U);
            var xFraction = ((cell % 4) + random.NextDouble()) / 4.0;
            var yFraction = ((cell / 4) + random.NextDouble()) / 4.0;
            var translation = new Point2(
                minimumX + ((maximumX - minimumX) * xFraction),
                minimumY + ((maximumY - minimumY) * yFraction));
            var candidate = new TargetCurve(
                kind,
                rotated.Transform(0, translation),
                flatteningTolerance,
                random.NextBoolean());
            if (IsValid(candidate, key.CanvasWidthDip, key.CanvasHeightDip, margin))
            {
                return GenerationResult<TargetCurve>.Success(candidate);
            }
        }

        var fallback = CreateFallback(
            key,
            settings,
            kind,
            flatteningTolerance,
            margin,
            diversitySeed);
        return fallback is null
            ? GenerationResult<TargetCurve>.Failure(new GenerationError(
                "GenerationFailed",
                "The requested line settings cannot produce a valid curve on this canvas."))
            : GenerationResult<TargetCurve>.Success(fallback, usedFallback: true);
    }

    internal static CubicBezier2 CreateLocalCurve(
        CurveKind kind,
        double desiredArcLength,
        double signedCurvatureRatio,
        double flatteningTolerance)
    {
        var curvature = kind == CurveKind.Straight ? 0 : signedCurvatureRatio;
        CubicBezier2 unit;
        switch (kind)
        {
            case CurveKind.Straight:
                unit = new CubicBezier2(
                    new Point2(-0.5, 0),
                    new Point2(-1.0 / 6.0, 0),
                    new Point2(1.0 / 6.0, 0),
                    new Point2(0.5, 0));
                break;
            case CurveKind.CShape:
            {
                var start = new Point2(-0.5, 0);
                var end = new Point2(0.5, 0);
                var quadraticControl = new Point2(0, curvature * 2);
                unit = new CubicBezier2(
                    start,
                    start + ((quadraticControl - start) * (2.0 / 3.0)),
                    end + ((quadraticControl - end) * (2.0 / 3.0)),
                    end);
                break;
            }
            case CurveKind.SShape:
                unit = new CubicBezier2(
                    new Point2(-0.5, 0),
                    new Point2(-1.0 / 6.0, curvature * 2),
                    new Point2(1.0 / 6.0, -curvature * 2),
                    new Point2(0.5, 0));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var unitLength = GeometryMath.PolylineLength(
            GeometryMath.FlattenBezier(unit, Math.Min(flatteningTolerance, 0.001)));
        var scale = unitLength > GeometryMath.Epsilon
            ? desiredArcLength / unitLength
            : desiredArcLength;
        return unit.Transform(0, Point2.Zero, scale);
    }

    internal static CurveKind SelectKind(
        ref Pcg32 random,
        double straightWeight,
        double cShapeWeight,
        double sShapeWeight)
    {
        var maximumWeight = Math.Max(straightWeight, Math.Max(cShapeWeight, sShapeWeight));
        var normalizedStraight = straightWeight / maximumWeight;
        var normalizedCShape = cShapeWeight / maximumWeight;
        var normalizedSShape = sShapeWeight / maximumWeight;
        var choice = random.NextDouble() *
                     (normalizedStraight + normalizedCShape + normalizedSShape);
        if (choice < normalizedStraight)
        {
            return CurveKind.Straight;
        }

        return choice < normalizedStraight + normalizedCShape
            ? CurveKind.CShape
            : CurveKind.SShape;
    }

    private static GenerationError? Validate(
        GenerationKey key,
        LineGenerationSettings settings)
    {
        if (!double.IsFinite(key.CanvasWidthDip) || !double.IsFinite(key.CanvasHeightDip) ||
            key.CanvasWidthDip <= 0 || key.CanvasHeightDip <= 0)
        {
            return new GenerationError("InvalidCanvas", "Canvas dimensions must be finite and positive.");
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
            settings.MaximumLengthRatio > 0.95)
        {
            return new GenerationError("InvalidLength", "Length ratios are invalid.", "LengthRatio");
        }

        if (!double.IsFinite(settings.MinimumCurvatureRatio) ||
            !double.IsFinite(settings.MaximumCurvatureRatio) ||
            settings.MinimumCurvatureRatio < 0 ||
            settings.MaximumCurvatureRatio < settings.MinimumCurvatureRatio ||
            settings.MaximumCurvatureRatio > 0.6)
        {
            return new GenerationError("InvalidCurvature", "Curvature ratios are invalid.", "CurvatureRatio");
        }

        if (!double.IsFinite(settings.MinimumDirectionDegrees) ||
            !double.IsFinite(settings.MaximumDirectionDegrees) ||
            settings.MaximumDirectionDegrees <= settings.MinimumDirectionDegrees ||
            settings.MaximumDirectionDegrees - settings.MinimumDirectionDegrees > 360)
        {
            return new GenerationError("InvalidDirection", "Direction range must span more than 0 and at most 360 degrees.");
        }

        if (!double.IsFinite(settings.SafeMarginRatio) ||
            settings.SafeMarginRatio is < 0 or >= 0.45)
        {
            return new GenerationError("InvalidMargin", "Safe margin ratio is invalid.", "SafeMarginRatio");
        }

        if (settings.MaximumAttempts is < 1 or > 4096)
        {
            return new GenerationError("InvalidAttempts", "Maximum attempts must be between 1 and 4096.");
        }

        return null;
    }

    private static bool AreFiniteAndNonNegative(params double[] values) =>
        values.All(static value => double.IsFinite(value) && value >= 0);

    private static bool IsValid(
        TargetCurve candidate,
        double width,
        double height,
        double margin)
    {
        var safeBounds = new Rect2(
            margin,
            margin,
            width - (2 * margin),
            height - (2 * margin));
        if (!safeBounds.Contains(new Point2(candidate.Bounds.Left, candidate.Bounds.Top), 1e-7) ||
            !safeBounds.Contains(new Point2(candidate.Bounds.Right, candidate.Bounds.Bottom), 1e-7) ||
            candidate.Length <= GeometryMath.Epsilon ||
            GeometryMath.HasSelfIntersection(candidate.Polyline))
        {
            return false;
        }

        for (var index = 0; index <= 64; index++)
        {
            if (candidate.Bezier.Derivative(index / 64.0).Length <= 1e-6)
            {
                return false;
            }
        }

        var separation = Math.Max(candidate.Length * 0.015, 1e-5);
        return !GeometryMath.HasNonAdjacentNearOverlap(
            candidate.Polyline,
            separation,
            Math.Max(5, candidate.Polyline.Count / 8));
    }

    private static TargetCurve? CreateFallback(
        GenerationKey key,
        LineGenerationSettings settings,
        CurveKind kind,
        double tolerance,
        double margin,
        ulong diversitySeed)
    {
        var shortSide = Math.Min(key.CanvasWidthDip, key.CanvasHeightDip);
        var length = shortSide * settings.MinimumLengthRatio;
        var exerciseLane = unchecked((uint)key.ExerciseIndex);
        var directionPhase = (int)(diversitySeed & 15);
        var curvaturePhase = (int)((diversitySeed >> 16) & 7);
        // Keep the fallback in the same well-separated 4x4 lane sequence used by
        // the primary attempt. This prevents a valid primary question followed by
        // a fallback question from accidentally collapsing into the same region.
        var positionCell = (int)((exerciseLane * 7U) & 15U);
        var xFraction = ((positionCell % 4) + 0.5) / 4.0;
        var yFraction = ((positionCell / 4) + 0.5) / 4.0;

        for (var directionOrdinal = 0; directionOrdinal < 18; directionOrdinal++)
        {
            double directionFraction;
            if (directionOrdinal < 16)
            {
                var directionBin = (int)(
                    (directionPhase +
                     (exerciseLane * 5U) +
                     ((uint)directionOrdinal * 7U)) & 15U);
                directionFraction = (directionBin + 0.5) / 16.0;
            }
            else
            {
                var firstEndpoint = ((exerciseLane + (uint)directionPhase) & 1U) == 0;
                directionFraction = directionOrdinal == 16
                    ? firstEndpoint ? 0 : 1
                    : firstEndpoint ? 1 : 0;
            }

            var curvatureCandidateCount = kind == CurveKind.Straight ? 1 : 16;
            for (var curvatureOrdinal = 0;
                 curvatureOrdinal < curvatureCandidateCount;
                 curvatureOrdinal++)
            {
                var signedCurvature = 0.0;
                if (kind != CurveKind.Straight)
                {
                    var magnitudeOrdinal = curvatureOrdinal / 2;
                    var magnitudeBin = (int)(
                        (curvaturePhase +
                         (exerciseLane * 3U) +
                         ((uint)magnitudeOrdinal * 5U)) & 7U);
                    var magnitudeFraction = (magnitudeBin + 0.5) / 8.0;
                    var magnitude = settings.MinimumCurvatureRatio +
                                    ((settings.MaximumCurvatureRatio -
                                      settings.MinimumCurvatureRatio) * magnitudeFraction);
                    var positiveFirst = ((exerciseLane +
                                          (uint)curvaturePhase +
                                          (uint)magnitudeOrdinal) & 1U) == 0;
                    var positive = (curvatureOrdinal & 1) == 0
                        ? positiveFirst
                        : !positiveFirst;
                    signedCurvature = positive ? magnitude : -magnitude;
                }

                var local = CreateLocalCurve(kind, length, signedCurvature, tolerance);
                var angleDegrees = settings.MinimumDirectionDegrees +
                                   ((settings.MaximumDirectionDegrees - settings.MinimumDirectionDegrees) *
                                    directionFraction);
                var rotated = local.Transform(angleDegrees * Math.PI / 180, Point2.Zero);
                var bounds = GeometryMath.BezierBounds(rotated);
                var minimumX = margin - bounds.Left;
                var maximumX = key.CanvasWidthDip - margin - bounds.Right;
                var minimumY = margin - bounds.Top;
                var maximumY = key.CanvasHeightDip - margin - bounds.Bottom;
                if (maximumX < minimumX || maximumY < minimumY)
                {
                    continue;
                }

                var translation = new Point2(
                    minimumX + ((maximumX - minimumX) * xFraction),
                    minimumY + ((maximumY - minimumY) * yFraction));
                var target = new TargetCurve(
                    kind,
                    rotated.Transform(0, translation),
                    tolerance,
                    suggestedForward:
                        ((exerciseLane + (uint)(diversitySeed >> 24)) & 1U) == 0);
                if (IsValid(target, key.CanvasWidthDip, key.CanvasHeightDip, margin))
                {
                    return target;
                }
            }
        }

        return null;
    }
}
