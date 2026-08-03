using System.Collections.ObjectModel;
using DrawAim.Infrastructure.History;

namespace DrawAim.App.Services;

public readonly record struct ScoreComparisonKey(
    TrainingModeKind Mode,
    string ScoringVersion,
    string SettingsFingerprint,
    bool UsesStrokeStabilization)
{
    public static ScoreComparisonKey FromEntry(ExerciseHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ScoreComparisonKey(
            entry.Mode,
            Normalize(entry.ScoringVersion),
            Normalize(entry.SettingsFingerprint),
            entry.StrokeStabilization > 0);
    }

    internal static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record BestScoreQuery(
    TrainingModeKind Mode,
    string? ScoringVersion = null,
    string? SettingsFingerprint = null,
    bool? UsesStrokeStabilization = null);

public sealed record BestScoreSnapshot(
    Guid EntryId,
    DateTimeOffset TimestampUtc,
    ScoreComparisonKey ComparisonKey,
    double TotalScore,
    IReadOnlyDictionary<string, double> ComponentScores)
{
    internal static BestScoreSnapshot? TryCreate(ExerciseHistoryEntry entry)
    {
        if (entry.Outcome != ExerciseOutcome.Completed ||
            entry.TotalScore is null ||
            !double.IsFinite(entry.TotalScore.Value))
        {
            return null;
        }

        var componentScoreValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double value) in
            entry.ComponentScores ?? new Dictionary<string, double>())
        {
            if (!string.IsNullOrWhiteSpace(name) && double.IsFinite(value))
            {
                componentScoreValues[name.Trim()] = Math.Clamp(value, 0.0, 100.0);
            }
        }

        var componentScores = new ReadOnlyDictionary<string, double>(componentScoreValues);

        return new BestScoreSnapshot(
            entry.Id,
            entry.TimestampUtc.ToUniversalTime(),
            ScoreComparisonKey.FromEntry(entry),
            Math.Clamp(entry.TotalScore.Value, 0.0, 100.0),
            componentScores);
    }
}

public sealed record ModeSessionStatisticsSnapshot(
    TrainingModeKind Mode,
    int TotalAttempts,
    int Completed,
    int Skipped,
    int SystemCancelled,
    int ScoredExercises,
    double? AverageScore,
    double? BestScore);

public sealed record SessionStatisticsSnapshot(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CapturedAtUtc,
    int TotalAttempts,
    int Completed,
    int Skipped,
    int SystemCancelled,
    int ScoredExercises,
    double? AverageScore,
    double? BestScore,
    IReadOnlyDictionary<TrainingModeKind, ModeSessionStatisticsSnapshot> Modes);

/// <summary>Thread-safe in-memory statistics for the current application session.</summary>
public sealed class SessionStatistics
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<TrainingModeKind, MutableModeStatistics> _modes = [];
    private readonly Dictionary<ScoreComparisonKey, BestScoreSnapshot> _bestByKey = [];
    private DateTimeOffset _startedAtUtc;

    public SessionStatistics(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAtUtc = _timeProvider.GetUtcNow();
    }

    public void Record(ExerciseHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        BestScoreSnapshot? score = BestScoreSnapshot.TryCreate(entry);

        lock (_sync)
        {
            if (!_modes.TryGetValue(entry.Mode, out MutableModeStatistics? mode))
            {
                mode = new MutableModeStatistics(entry.Mode);
                _modes.Add(entry.Mode, mode);
            }

            mode.TotalAttempts++;
            switch (entry.Outcome)
            {
                case ExerciseOutcome.Completed:
                    mode.Completed++;
                    break;
                case ExerciseOutcome.Skipped:
                    mode.Skipped++;
                    break;
                case ExerciseOutcome.SystemCancelled:
                    mode.SystemCancelled++;
                    break;
            }

            if (score is not null)
            {
                mode.ScoredExercises++;
                mode.ScoreSum += score.TotalScore;
                mode.BestScore = mode.BestScore is null
                    ? score.TotalScore
                    : Math.Max(mode.BestScore.Value, score.TotalScore);
                AddBestWithoutLock(score);
            }
        }
    }

    public SessionStatisticsSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            ModeSessionStatisticsSnapshot[] modeSnapshots = _modes.Values
                .OrderBy(static mode => mode.Mode)
                .Select(static mode => mode.ToSnapshot())
                .ToArray();

            int scored = modeSnapshots.Sum(static mode => mode.ScoredExercises);
            double scoreSum = _modes.Values.Sum(static mode => mode.ScoreSum);
            double? best = modeSnapshots
                .Where(static mode => mode.BestScore is not null)
                .Select(static mode => mode.BestScore!.Value)
                .DefaultIfEmpty()
                .Max();

            if (scored == 0)
            {
                best = null;
            }

            var modes = new ReadOnlyDictionary<TrainingModeKind, ModeSessionStatisticsSnapshot>(
                modeSnapshots.ToDictionary(static mode => mode.Mode));

            return new SessionStatisticsSnapshot(
                _startedAtUtc,
                _timeProvider.GetUtcNow(),
                modeSnapshots.Sum(static mode => mode.TotalAttempts),
                modeSnapshots.Sum(static mode => mode.Completed),
                modeSnapshots.Sum(static mode => mode.Skipped),
                modeSnapshots.Sum(static mode => mode.SystemCancelled),
                scored,
                scored == 0 ? null : scoreSum / scored,
                best,
                modes);
        }
    }

    public BestScoreSnapshot? GetBest(BestScoreQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_sync)
        {
            return FindBest(_bestByKey.Values, query);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _modes.Clear();
            _bestByKey.Clear();
            _startedAtUtc = _timeProvider.GetUtcNow();
        }
    }

    internal static BestScoreSnapshot? FindBest(
        IEnumerable<BestScoreSnapshot> candidates,
        BestScoreQuery query)
    {
        string? scoringVersion = query.ScoringVersion is null
            ? null
            : ScoreComparisonKey.Normalize(query.ScoringVersion);
        string? settingsFingerprint = query.SettingsFingerprint is null
            ? null
            : ScoreComparisonKey.Normalize(query.SettingsFingerprint);

        return candidates
            .Where(candidate => candidate.ComparisonKey.Mode == query.Mode)
            .Where(candidate => scoringVersion is null ||
                string.Equals(
                    candidate.ComparisonKey.ScoringVersion,
                    scoringVersion,
                    StringComparison.OrdinalIgnoreCase))
            .Where(candidate => settingsFingerprint is null ||
                string.Equals(
                    candidate.ComparisonKey.SettingsFingerprint,
                    settingsFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            .Where(candidate => query.UsesStrokeStabilization is null ||
                candidate.ComparisonKey.UsesStrokeStabilization == query.UsesStrokeStabilization.Value)
            .OrderByDescending(static candidate => candidate.TotalScore)
            .ThenByDescending(static candidate => candidate.TimestampUtc)
            .FirstOrDefault();
    }

    internal static bool IsBetter(BestScoreSnapshot candidate, BestScoreSnapshot current) =>
        candidate.TotalScore > current.TotalScore ||
        (candidate.TotalScore.Equals(current.TotalScore) &&
            candidate.TimestampUtc > current.TimestampUtc);

    private void AddBestWithoutLock(BestScoreSnapshot candidate)
    {
        if (!_bestByKey.TryGetValue(candidate.ComparisonKey, out BestScoreSnapshot? current) ||
            IsBetter(candidate, current))
        {
            _bestByKey[candidate.ComparisonKey] = candidate;
        }
    }

    private sealed class MutableModeStatistics(TrainingModeKind mode)
    {
        public TrainingModeKind Mode { get; } = mode;

        public int TotalAttempts { get; set; }

        public int Completed { get; set; }

        public int Skipped { get; set; }

        public int SystemCancelled { get; set; }

        public int ScoredExercises { get; set; }

        public double ScoreSum { get; set; }

        public double? BestScore { get; set; }

        public ModeSessionStatisticsSnapshot ToSnapshot() =>
            new(
                Mode,
                TotalAttempts,
                Completed,
                Skipped,
                SystemCancelled,
                ScoredExercises,
                ScoredExercises == 0 ? null : ScoreSum / ScoredExercises,
                BestScore);
    }
}
