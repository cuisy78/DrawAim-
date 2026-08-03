using System.Globalization;
using System.Text.Json;
using DrawAim.Core.Generation;
using DrawAim.Infrastructure.Settings;

namespace DrawAim.App;

public partial class MainWindow
{
    private void ApplyExtendedLoadedSettings(AppSettings settings)
    {
        ModeSettings mode1 = settings.ModeOne;
        Mode1LineKind.SelectedIndex = GetIntOption(mode1, "LineKind", 3, 0, 3);
        Mode1StraightWeight.Value = GetLineWeight(mode1, "Straight", 34);
        Mode1CWeight.Value = GetLineWeight(mode1, "C", 33);
        Mode1SWeight.Value = GetLineWeight(mode1, "S", 33);
        Mode1Difficulty.Value = GetDifficulty(mode1, 5);
        Mode1Length.Value = GetDoubleOption(mode1, "Length", 52, 25, 80);
        Mode1Curvature.Value = GetDoubleOption(mode1, "Curvature", 18, 5, 32);
        Mode1DirectionMin.Value = GetDoubleOption(mode1, "DirectionMin", 0, 0, 359);
        Mode1DirectionMax.Value = GetDoubleOption(mode1, "DirectionMax", 360, 1, 360);
        Mode1AnswerWidth.Value = GetDoubleOption(mode1, "AnswerWidth", 4, 1, 24);
        Mode1TargetWidth.Value = GetDoubleOption(mode1, "TargetWidth", 3, 1, 12);
        Mode1Tolerance.Value = GetDoubleOption(mode1, "Tolerance", 14, 5, 30);
        Mode1ShowHint.IsChecked = GetBoolOption(mode1, "ShowHint", true);

        ModeSettings mode2 = settings.ModeTwo;
        Mode2UseCountRange.IsChecked = GetBoolOption(mode2, "UseCountRange", false);
        Mode2LineCount.Value = GetDoubleOption(mode2, "FixedCount", 5, 1, 10);
        Mode2MinCount.Value = GetDoubleOption(mode2, "MinimumCount", 3, 1, 10);
        Mode2MaxCount.Value = GetDoubleOption(mode2, "MaximumCount", 7, 1, 10);
        Mode2StraightWeight.Value = GetLineWeight(mode2, "Straight", 34);
        Mode2CWeight.Value = GetLineWeight(mode2, "C", 33);
        Mode2SWeight.Value = GetLineWeight(mode2, "S", 33);
        Mode2Difficulty.Value = GetDifficulty(mode2, 5);
        Mode2MinLength.Value = GetDoubleOption(mode2, "MinimumLength", 17, 10, 70);
        Mode2MaxLength.Value = GetDoubleOption(mode2, "MaximumLength", 46, 10, 70);
        Mode2MinCurvature.Value = GetDoubleOption(mode2, "MinimumCurvature", 5, 0, 45);
        Mode2MaxCurvature.Value = GetDoubleOption(mode2, "MaximumCurvature", 23, 0, 45);
        Mode2AllowIntersections.IsChecked = GetBoolOption(mode2, "AllowIntersections", false);

        ModeSettings mode3 = settings.ModeThree;
        Mode3Difficulty.Value = GetDifficulty(mode3, 5);
        Mode3IncludeWhite.IsChecked = GetBoolOption(mode3, "IncludeWhite", false);
        Mode3IncludeBlack.IsChecked = GetBoolOption(mode3, "IncludeBlack", false);
        Mode3IncludeLowChroma.IsChecked = GetBoolOption(mode3, "IncludeLowChroma", true);
        Mode3PracticeMode.IsChecked = GetBoolOption(mode3, "PracticeMode", true);
        Mode1LockSeed.IsChecked = GetBoolOption(mode1, "LockSeed", false);
        Mode2LockSeed.IsChecked = GetBoolOption(mode2, "LockSeed", false);
        Mode3LockSeed.IsChecked = GetBoolOption(mode3, "LockSeed", false);
        if (mode3.Seed != 0)
        {
            Mode3Seed.Text = mode3.Seed.ToString(CultureInfo.InvariantCulture);
        }

        Mode1Settings_Changed(this, EventArgs.Empty);
        Mode2Settings_Changed(this, EventArgs.Empty);
        Mode3Settings_Changed(this, EventArgs.Empty);
    }

    private void CaptureExtendedSettings()
    {
        _settings.ModeOne.GeneratorVersion = TargetLineGenerator.Version;
        _settings.ModeTwo.GeneratorVersion = MultiLineGenerator.Version;
        _settings.ModeThree.GeneratorVersion = TargetColorGenerator.Version;
        _settings.ModeOne.Difficulty = $"{Mode1Difficulty.Value:F0}";
        _settings.ModeOne.Seed = ParseSeed(Mode1Seed.Text, 20260803);
        _settings.ModeOne.LineTypeWeights = CreateLineWeights(
            Mode1StraightWeight.Value,
            Mode1CWeight.Value,
            Mode1SWeight.Value);
        SetOption(_settings.ModeOne, "LineKind", Mode1LineKind.SelectedIndex);
        SetOption(_settings.ModeOne, "Length", Mode1Length.Value);
        SetOption(_settings.ModeOne, "Curvature", Mode1Curvature.Value);
        SetOption(_settings.ModeOne, "DirectionMin", Mode1DirectionMin.Value);
        SetOption(_settings.ModeOne, "DirectionMax", Mode1DirectionMax.Value);
        SetOption(_settings.ModeOne, "AnswerWidth", Mode1AnswerWidth.Value);
        SetOption(_settings.ModeOne, "TargetWidth", Mode1TargetWidth.Value);
        SetOption(_settings.ModeOne, "Tolerance", Mode1Tolerance.Value);
        SetOption(_settings.ModeOne, "ShowHint", Mode1ShowHint.IsChecked == true);
        SetOption(_settings.ModeOne, "LockSeed", Mode1LockSeed.IsChecked == true);

        _settings.ModeTwo.Difficulty = $"{Mode2Difficulty.Value:F0}";
        _settings.ModeTwo.Seed = ParseSeed(Mode2Seed.Text, 20260803);
        _settings.ModeTwo.LineTypeWeights = CreateLineWeights(
            Mode2StraightWeight.Value,
            Mode2CWeight.Value,
            Mode2SWeight.Value);
        SetOption(_settings.ModeTwo, "UseCountRange", Mode2UseCountRange.IsChecked == true);
        SetOption(_settings.ModeTwo, "FixedCount", Mode2LineCount.Value);
        SetOption(_settings.ModeTwo, "MinimumCount", Mode2MinCount.Value);
        SetOption(_settings.ModeTwo, "MaximumCount", Mode2MaxCount.Value);
        SetOption(_settings.ModeTwo, "MinimumLength", Mode2MinLength.Value);
        SetOption(_settings.ModeTwo, "MaximumLength", Mode2MaxLength.Value);
        SetOption(_settings.ModeTwo, "MinimumCurvature", Mode2MinCurvature.Value);
        SetOption(_settings.ModeTwo, "MaximumCurvature", Mode2MaxCurvature.Value);
        SetOption(_settings.ModeTwo, "AllowIntersections", Mode2AllowIntersections.IsChecked == true);
        SetOption(_settings.ModeTwo, "LockSeed", Mode2LockSeed.IsChecked == true);

        _settings.ModeThree.Difficulty = $"{Mode3Difficulty.Value:F0}";
        _settings.ModeThree.Seed = ParseSeed(Mode3Seed.Text, 20260803);
        SetOption(_settings.ModeThree, "IncludeWhite", Mode3IncludeWhite.IsChecked == true);
        SetOption(_settings.ModeThree, "IncludeBlack", Mode3IncludeBlack.IsChecked == true);
        SetOption(_settings.ModeThree, "IncludeLowChroma", Mode3IncludeLowChroma.IsChecked == true);
        SetOption(_settings.ModeThree, "PracticeMode", Mode3PracticeMode.IsChecked == true);
        SetOption(_settings.ModeThree, "LockSeed", Mode3LockSeed.IsChecked == true);
    }

    private static Dictionary<string, double> CreateLineWeights(
        double straight,
        double cShape,
        double sShape) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Straight"] = Math.Clamp(straight, 0, 100),
            ["C"] = Math.Clamp(cShape, 0, 100),
            ["S"] = Math.Clamp(sShape, 0, 100),
        };

    private static double GetLineWeight(ModeSettings settings, string name, double fallback) =>
        settings.LineTypeWeights.TryGetValue(name, out double value) &&
        double.IsFinite(value) && value >= 0
            ? Math.Clamp(value, 0, 100)
            : fallback;

    private static int GetDifficulty(ModeSettings settings, int fallback) =>
        int.TryParse(settings.Difficulty, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, 1, 10)
            : fallback;

    private static int GetIntOption(
        ModeSettings settings,
        string key,
        int fallback,
        int minimum,
        int maximum) => (int)Math.Round(GetDoubleOption(
            settings,
            key,
            fallback,
            minimum,
            maximum));

    private static double GetDoubleOption(
        ModeSettings settings,
        string key,
        double fallback,
        double minimum,
        double maximum)
    {
        if (!settings.Options.TryGetValue(key, out JsonElement option) ||
            option.ValueKind != JsonValueKind.Number ||
            !option.TryGetDouble(out double value) ||
            !double.IsFinite(value))
        {
            return fallback;
        }

        return Math.Clamp(value, minimum, maximum);
    }

    private static bool GetBoolOption(
        ModeSettings settings,
        string key,
        bool fallback)
    {
        if (!settings.Options.TryGetValue(key, out JsonElement option))
        {
            return fallback;
        }

        return option.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static void SetOption<T>(ModeSettings settings, string key, T value) =>
        settings.Options[key] = JsonSerializer.SerializeToElement(value);

    private static (double Minimum, double Maximum) NormalizeOrderedRange(
        double first,
        double second,
        double allowedMinimum,
        double allowedMaximum,
        double minimumSpan)
    {
        double minimum = Math.Clamp(Math.Min(first, second), allowedMinimum, allowedMaximum);
        double maximum = Math.Clamp(Math.Max(first, second), allowedMinimum, allowedMaximum);
        if (maximum - minimum >= minimumSpan)
        {
            return (minimum, maximum);
        }

        if (minimum + minimumSpan <= allowedMaximum)
        {
            maximum = minimum + minimumSpan;
        }
        else
        {
            minimum = maximum - minimumSpan;
        }

        return (minimum, maximum);
    }
}
