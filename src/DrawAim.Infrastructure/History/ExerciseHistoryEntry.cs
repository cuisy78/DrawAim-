namespace DrawAim.Infrastructure.History;

public enum TrainingModeKind
{
    LineFollow = 1,
    ObservationCopy = 2,
    ColorMatch = 3,
}

public enum ExerciseOutcome
{
    Completed = 1,
    Skipped = 2,
    SystemCancelled = 3,
}

/// <summary>A normalized, summary-only history record. It never stores full stroke data.</summary>
public sealed class ExerciseHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public TrainingModeKind Mode { get; set; }

    public ExerciseOutcome Outcome { get; set; } = ExerciseOutcome.Completed;

    public ulong Seed { get; set; }

    public long ExerciseIndex { get; set; }

    public string GeneratorVersion { get; set; } = "Unknown";

    public string SettingsFingerprint { get; set; } = string.Empty;

    public int StrokeStabilization { get; set; }

    public string StabilizerVersion { get; set; } = "StrokeStabilizerV1";

    public string ScoringVersion { get; set; } = "Unknown";

    public double? TotalScore { get; set; }

    public Dictionary<string, double> ComponentScores { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal static ExerciseHistoryEntry Normalize(ExerciseHistoryEntry value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new ExerciseHistoryEntry
        {
            Id = value.Id == Guid.Empty ? Guid.NewGuid() : value.Id,
            TimestampUtc = value.TimestampUtc == default
                ? DateTimeOffset.UtcNow
                : value.TimestampUtc.ToUniversalTime(),
            Mode = Enum.IsDefined(value.Mode) ? value.Mode : TrainingModeKind.LineFollow,
            Outcome = Enum.IsDefined(value.Outcome) ? value.Outcome : ExerciseOutcome.Completed,
            Seed = value.Seed,
            ExerciseIndex = Math.Max(0, value.ExerciseIndex),
            GeneratorVersion = NormalizeText(value.GeneratorVersion, "Unknown"),
            SettingsFingerprint = value.SettingsFingerprint?.Trim() ?? string.Empty,
            StrokeStabilization = Math.Clamp(value.StrokeStabilization, 0, 100),
            StabilizerVersion = NormalizeText(value.StabilizerVersion, "StrokeStabilizerV1"),
            ScoringVersion = NormalizeText(value.ScoringVersion, "Unknown"),
            TotalScore = NormalizeScore(value.TotalScore),
            ComponentScores = NormalizeComponentScores(value.ComponentScores),
        };
    }

    private static Dictionary<string, double> NormalizeComponentScores(
        Dictionary<string, double>? source)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (source is null)
        {
            return result;
        }

        foreach ((string key, double score) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && double.IsFinite(score))
            {
                // Components may represent signed diagnostic deltas (for example,
                // color lightness or saturation error), rather than percentages.
                result[key.Trim()] = score;
            }
        }

        return result;
    }

    private static double? NormalizeScore(double? score) =>
        score is not null && double.IsFinite(score.Value)
            ? Math.Clamp(score.Value, 0.0, 100.0)
            : null;

    private static string NormalizeText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
