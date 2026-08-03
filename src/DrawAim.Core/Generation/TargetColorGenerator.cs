using DrawAim.Core.Color;
using DrawAim.Core.Randomness;

namespace DrawAim.Core.Generation;

public sealed class TargetColorGenerator
{
    public const string Version = "ColorGeneratorV1";

    public GenerationResult<ColorTarget> Generate(
        GenerationKey key,
        ColorGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = Validate(settings);
        if (validation is not null)
        {
            return GenerationResult<ColorTarget>.Failure(validation);
        }

        var seed = SeedDerivation.Derive(
            key,
            SeedDerivation.Quantize(settings.MinimumLightness),
            SeedDerivation.Quantize(settings.MaximumLightness),
            SeedDerivation.Quantize(settings.MinimumChroma),
            SeedDerivation.Quantize(settings.MaximumChroma),
            settings.IncludeNearWhite ? 1 : 0,
            settings.IncludeNearBlack ? 1 : 0,
            settings.IncludeLowChroma ? 1 : 0,
            settings.Difficulty,
            SeedDerivation.Quantize(settings.MinimumPreviousDeltaE));
        var random = new Pcg32(seed);
        OklchColor lastCandidate = default;

        for (var attempt = 0; attempt < settings.MaximumAttempts; attempt++)
        {
            var lightness = SelectLightness(ref random, settings);
            var chroma = settings.IncludeLowChroma && random.NextDouble() < 0.20
                ? random.NextDouble(0, Math.Min(0.035, settings.MaximumChroma))
                : random.NextDouble(settings.MinimumChroma, settings.MaximumChroma);
            var hueFraction = attempt == 0
                ? (((uint)key.ExerciseIndex % 24) + random.NextDouble()) / 24.0
                : random.NextDouble();
            var hue = hueFraction * 360;
            lastCandidate = new OklchColor(lightness, chroma, hue);
            var lab = ColorMath.OklchToOklab(lastCandidate);
            var srgb = ColorMath.OklabToSrgb(lab, clamp: false);
            if (!srgb.IsInGamut())
            {
                continue;
            }

            srgb = srgb.Clamp();
            if (settings.PreviousColor is SrgbColor previous &&
                ColorMath.DeltaEOK(
                    ColorMath.SrgbToOklab(previous),
                    ColorMath.SrgbToOklab(srgb)) < settings.MinimumPreviousDeltaE)
            {
                continue;
            }

            return GenerationResult<ColorTarget>.Success(
                new ColorTarget(srgb, lab, lastCandidate));
        }

        for (var lightnessStep = 0; lightnessStep <= 8; lightnessStep++)
        {
            var lightnessFraction = lightnessStep / 8.0;
            var fallbackLightness = settings.MinimumLightness +
                                    ((settings.MaximumLightness - settings.MinimumLightness) *
                                     lightnessFraction);
            for (var chromaStep = 0; chromaStep <= 8; chromaStep++)
            {
                var chromaFraction = chromaStep / 8.0;
                var fallbackChroma = settings.MaximumChroma -
                                     ((settings.MaximumChroma - settings.MinimumChroma) *
                                      chromaFraction);
                for (var hueStep = 0; hueStep < 72; hueStep++)
                {
                    var fallbackHue = (((uint)key.ExerciseIndex + (uint)hueStep) % 72) * 5.0;
                    var fallbackLch = new OklchColor(
                        fallbackLightness,
                        fallbackChroma,
                        fallbackHue);
                    var fallbackLab = ColorMath.OklchToOklab(fallbackLch);
                    var fallbackSrgb = ColorMath.OklabToSrgb(fallbackLab, clamp: false);
                    if (!fallbackSrgb.IsInGamut())
                    {
                        continue;
                    }

                    fallbackSrgb = fallbackSrgb.Clamp();
                    if (settings.PreviousColor is SrgbColor previous &&
                        ColorMath.DeltaEOK(
                            ColorMath.SrgbToOklab(previous),
                            ColorMath.SrgbToOklab(fallbackSrgb)) < settings.MinimumPreviousDeltaE)
                    {
                        continue;
                    }

                    return GenerationResult<ColorTarget>.Success(
                        new ColorTarget(fallbackSrgb, fallbackLab, fallbackLch),
                        usedFallback: true);
                }
            }
        }

        return GenerationResult<ColorTarget>.Failure(new GenerationError(
            "ColorGenerationFailed",
            "No sRGB color satisfies the requested lightness, chroma and previous-color constraints."));
    }

    private static GenerationError? Validate(ColorGenerationSettings settings)
    {
        if (!double.IsFinite(settings.MinimumLightness) ||
            !double.IsFinite(settings.MaximumLightness) ||
            settings.MinimumLightness < 0 ||
            settings.MaximumLightness > 1 ||
            settings.MaximumLightness < settings.MinimumLightness)
        {
            return new GenerationError("InvalidLightness", "Lightness range must be inside 0 to 1.");
        }

        if (!double.IsFinite(settings.MinimumChroma) ||
            !double.IsFinite(settings.MaximumChroma) ||
            settings.MinimumChroma < 0 ||
            settings.MaximumChroma < settings.MinimumChroma ||
            settings.MaximumChroma > 0.5)
        {
            return new GenerationError("InvalidChroma", "Chroma range is invalid.");
        }

        if (!double.IsFinite(settings.MinimumPreviousDeltaE) ||
            settings.MinimumPreviousDeltaE is < 0 or > 100)
        {
            return new GenerationError("InvalidPreviousDistance", "Previous color distance is invalid.");
        }

        if (settings.PreviousColor is SrgbColor previous && !previous.IsFinite)
        {
            return new GenerationError("InvalidPreviousColor", "Previous color must be finite.");
        }

        if (settings.MaximumAttempts is < 1 or > 8192)
        {
            return new GenerationError("InvalidAttempts", "Maximum attempts must be between 1 and 8192.");
        }

        return null;
    }

    private static double SelectLightness(
        ref Pcg32 random,
        ColorGenerationSettings settings)
    {
        var bucket = random.NextDouble();
        var blackMinimum = Math.Max(0.02, settings.MinimumLightness);
        var blackMaximum = Math.Min(0.13, settings.MaximumLightness);
        if (settings.IncludeNearBlack && bucket < 0.10 && blackMaximum >= blackMinimum)
        {
            return random.NextDouble(blackMinimum, blackMaximum);
        }

        var whiteMinimum = Math.Max(0.88, settings.MinimumLightness);
        var whiteMaximum = Math.Min(0.98, settings.MaximumLightness);
        if (settings.IncludeNearWhite && bucket >= 0.90 && whiteMaximum >= whiteMinimum)
        {
            return random.NextDouble(whiteMinimum, whiteMaximum);
        }

        return random.NextDouble(settings.MinimumLightness, settings.MaximumLightness);
    }
}
