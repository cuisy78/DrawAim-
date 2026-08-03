using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DrawAim.Infrastructure.History;
using DrawAim.Infrastructure.Logging;
using DrawAim.Infrastructure.Settings;
using DrawAim.Infrastructure.Storage;

namespace DrawAim.App.Services;

public sealed record ApplicationDataInitializationResult(
    AppSettings Settings,
    StorageLoadStatus SettingsStatus,
    StorageLoadStatus HistoryStatus,
    int LoadedHistoryCount,
    IReadOnlyList<string> Warnings);

public sealed record ApplicationDataOperationResult(
    bool Succeeded,
    string? ErrorMessage = null)
{
    public static ApplicationDataOperationResult Success { get; } = new(true);
}

/// <summary>
/// Window-independent application data facade. Recoverable storage failures are
/// reported as result values and best-effort logs instead of escaping to the UI.
/// </summary>
public sealed class ApplicationDataService
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonExerciseHistoryStore _historyStore;
    private readonly RollingFileLogger _logger;
    private readonly SessionStatistics _sessionStatistics;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly Dictionary<ScoreComparisonKey, BestScoreSnapshot> _historicalBest = [];
    private AppSettings _currentSettings = AppSettings.CreateDefault();
    private ApplicationDataInitializationResult? _initializationResult;
    private bool _initialized;

    public ApplicationDataService()
        : this(CreateDefaultDependencies())
    {
    }

    public ApplicationDataService(
        JsonSettingsStore settingsStore,
        JsonExerciseHistoryStore historyStore,
        RollingFileLogger logger,
        SessionStatistics? sessionStatistics = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionStatistics = sessionStatistics ?? new SessionStatistics();
    }

    private ApplicationDataService(DefaultDependencies dependencies)
        : this(
            dependencies.SettingsStore,
            dependencies.HistoryStore,
            dependencies.Logger)
    {
    }

    public bool IsInitialized
    {
        get
        {
            lock (_stateSync)
            {
                return _initialized;
            }
        }
    }

    public AppSettings CurrentSettings
    {
        get
        {
            lock (_stateSync)
            {
                return CloneSettings(_currentSettings);
            }
        }
    }

    public async Task<ApplicationDataInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_stateSync)
        {
            if (_initializationResult is not null)
            {
                return _initializationResult;
            }
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateSync)
            {
                if (_initializationResult is not null)
                {
                    return _initializationResult;
                }
            }

            var warnings = new List<string>();
            SettingsLoadResult settingsResult = await LoadSettingsSafelyAsync(
                warnings,
                cancellationToken).ConfigureAwait(false);
            HistoryLoadResult historyResult = await LoadHistorySafelyAsync(
                warnings,
                cancellationToken).ConfigureAwait(false);

            lock (_stateSync)
            {
                _currentSettings = CloneSettings(settingsResult.Settings);
                _historicalBest.Clear();
                foreach (ExerciseHistoryEntry entry in historyResult.Entries)
                {
                    AddHistoricalBestWithoutLock(entry);
                }

                _initialized = true;
                _initializationResult = new ApplicationDataInitializationResult(
                    CloneSettings(_currentSettings),
                    settingsResult.Status,
                    historyResult.Status,
                    historyResult.Entries.Count,
                    new ReadOnlyCollection<string>(warnings.ToArray()));
                return _initializationResult;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<ApplicationDataOperationResult> SaveSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        AppSettings snapshot = CloneSettings(settings);

        lock (_stateSync)
        {
            _currentSettings = snapshot;
        }

        try
        {
            await _settingsStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return ApplicationDataOperationResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            await LogFailureAsync("保存设置失败。", exception).ConfigureAwait(false);
            return new ApplicationDataOperationResult(false, exception.Message);
        }
    }

    public async Task<ApplicationDataOperationResult> RecordExerciseAsync(
        ExerciseHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ExerciseHistoryEntry snapshot = CloneHistoryEntry(entry);

        _sessionStatistics.Record(snapshot);

        try
        {
            await _historyStore.AppendAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                AddHistoricalBestWithoutLock(snapshot);
            }

            return ApplicationDataOperationResult.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            await LogFailureAsync("记录题目历史失败；本次会话统计仍已保留。", exception)
                .ConfigureAwait(false);
            return new ApplicationDataOperationResult(false, exception.Message);
        }
    }

    public SessionStatisticsSnapshot GetSessionStatistics() =>
        _sessionStatistics.GetSnapshot();

    public BestScoreSnapshot? GetSessionBest(BestScoreQuery query) =>
        _sessionStatistics.GetBest(query);

    public BestScoreSnapshot? GetHistoricalBest(BestScoreQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_stateSync)
        {
            return SessionStatistics.FindBest(_historicalBest.Values, query);
        }
    }

    public void ResetSession() => _sessionStatistics.Reset();

    public async ValueTask<bool> LogAsync(
        DrawAimLogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            return await _logger.WriteAsync(level, message, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception loggingException) when (IsRecoverableStorageFailure(loggingException))
        {
            return false;
        }
    }

    private async Task<SettingsLoadResult> LoadSettingsSafelyAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            SettingsLoadResult result = await _settingsStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            AddLoadWarning("设置", result.Status, result.ErrorMessage, warnings);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            string warning = $"设置加载失败，已使用默认值：{exception.Message}";
            warnings.Add(warning);
            await LogFailureAsync(warning, exception).ConfigureAwait(false);
            return new SettingsLoadResult(
                AppSettings.CreateDefault(),
                StorageLoadStatus.UnavailableUsedDefaults,
                ErrorMessage: exception.Message);
        }
    }

    private async Task<HistoryLoadResult> LoadHistorySafelyAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            HistoryLoadResult result = await _historyStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            AddLoadWarning("历史", result.Status, result.ErrorMessage, warnings);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableStorageFailure(exception))
        {
            string warning = $"历史加载失败，已使用空历史：{exception.Message}";
            warnings.Add(warning);
            await LogFailureAsync(warning, exception).ConfigureAwait(false);
            return new HistoryLoadResult(
                ExerciseHistoryDocument.CurrentSchemaVersion,
                Array.Empty<ExerciseHistoryEntry>(),
                StorageLoadStatus.UnavailableUsedDefaults,
                ErrorMessage: exception.Message);
        }
    }

    private async ValueTask LogFailureAsync(string message, Exception exception)
    {
        try
        {
            await _logger.WriteAsync(DrawAimLogLevel.Error, message, exception)
                .ConfigureAwait(false);
        }
        catch (Exception loggingException) when (IsRecoverableStorageFailure(loggingException))
        {
            // A secondary logging failure must never hide the original storage result.
        }
    }

    private static void AddLoadWarning(
        string label,
        StorageLoadStatus status,
        string? errorMessage,
        List<string> warnings)
    {
        if (status is StorageLoadStatus.Loaded or StorageLoadStatus.MissingUsedDefaults)
        {
            return;
        }

        string message = status == StorageLoadStatus.CorruptRecovered
            ? $"{label}文件已损坏，原文件已备份并恢复。"
            : $"{label}文件当前不可用，已采用安全默认值。";
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            message = $"{message} {errorMessage}";
        }

        warnings.Add(message);
    }

    private void AddHistoricalBestWithoutLock(ExerciseHistoryEntry entry)
    {
        BestScoreSnapshot? candidate = BestScoreSnapshot.TryCreate(entry);
        if (candidate is null)
        {
            return;
        }

        if (!_historicalBest.TryGetValue(
                candidate.ComparisonKey,
                out BestScoreSnapshot? current) ||
            SessionStatistics.IsBetter(candidate, current))
        {
            _historicalBest[candidate.ComparisonKey] = candidate;
        }
    }

    private static AppSettings CloneSettings(AppSettings settings) =>
        new()
        {
            SchemaVersion = settings.SchemaVersion <= 0
                ? AppSettings.CurrentSchemaVersion
                : settings.SchemaVersion,
            Theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : "Dark",
            Culture = NormalizeText(settings.Culture, "zh-CN"),
            HasCompletedFirstRunGuide = settings.HasCompletedFirstRunGuide,
            BrushSize = double.IsFinite(settings.BrushSize)
                ? Math.Clamp(settings.BrushSize, 0.5, 256.0)
                : 8.0,
            ModeOne = CloneModeSettings(settings.ModeOne),
            ModeTwo = CloneModeSettings(settings.ModeTwo),
            ModeThree = CloneModeSettings(settings.ModeThree),
        };

    private static ModeSettings CloneModeSettings(ModeSettings? settings)
    {
        ModeSettings source = settings ?? new ModeSettings();
        return new ModeSettings
        {
            Difficulty = NormalizeText(source.Difficulty, "Normal"),
            Seed = source.Seed,
            GeneratorVersion = NormalizeText(source.GeneratorVersion, "GeneratorV1"),
            StrokeStabilization = Math.Clamp(source.StrokeStabilization, 0, 100),
            StabilizerVersion = NormalizeText(
                source.StabilizerVersion,
                "StrokeStabilizerV1"),
            LineTypeWeights = CloneLineTypeWeights(source.LineTypeWeights),
            Options = CloneModeOptions(source.Options),
        };
    }

    private static ExerciseHistoryEntry CloneHistoryEntry(ExerciseHistoryEntry entry) =>
        new()
        {
            Id = entry.Id,
            TimestampUtc = entry.TimestampUtc,
            Mode = entry.Mode,
            Outcome = entry.Outcome,
            Seed = entry.Seed,
            ExerciseIndex = entry.ExerciseIndex,
            GeneratorVersion = entry.GeneratorVersion,
            SettingsFingerprint = entry.SettingsFingerprint,
            StrokeStabilization = entry.StrokeStabilization,
            StabilizerVersion = entry.StabilizerVersion,
            ScoringVersion = entry.ScoringVersion,
            TotalScore = entry.TotalScore,
            ComponentScores = CloneComponentScores(entry.ComponentScores),
        };

    private static Dictionary<string, double> CloneLineTypeWeights(
        Dictionary<string, double>? values)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double value) in values ?? new Dictionary<string, double>())
        {
            if (!string.IsNullOrWhiteSpace(name) && double.IsFinite(value) && value >= 0)
            {
                result[name.Trim()] = value;
            }
        }

        if (result.Count != 0)
        {
            return result;
        }

        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Straight"] = 1.0,
            ["C"] = 1.0,
            ["S"] = 1.0,
        };
    }

    private static Dictionary<string, JsonElement> CloneModeOptions(
        Dictionary<string, JsonElement>? values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, JsonElement value) in
            values ?? new Dictionary<string, JsonElement>())
        {
            if (!string.IsNullOrWhiteSpace(name) && value.ValueKind != JsonValueKind.Undefined)
            {
                result[name.Trim()] = value.Clone();
            }
        }

        return result;
    }

    private static Dictionary<string, double> CloneComponentScores(
        Dictionary<string, double>? values)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double value) in values ?? new Dictionary<string, double>())
        {
            if (!string.IsNullOrWhiteSpace(name) && double.IsFinite(value))
            {
                result[name.Trim()] = Math.Clamp(value, 0.0, 100.0);
            }
        }

        return result;
    }

    private static bool IsRecoverableStorageFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException or
            InvalidOperationException or
            ArgumentException;

    private static string NormalizeText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static DefaultDependencies CreateDefaultDependencies()
    {
        DrawAimDataPaths paths = DrawAimDataPaths.Resolve();
        return new DefaultDependencies(
            new JsonSettingsStore(paths),
            new JsonExerciseHistoryStore(paths),
            new RollingFileLogger(paths));
    }

    private sealed record DefaultDependencies(
        JsonSettingsStore SettingsStore,
        JsonExerciseHistoryStore HistoryStore,
        RollingFileLogger Logger);
}
