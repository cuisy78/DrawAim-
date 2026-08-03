using System.Collections.ObjectModel;
using System.Text.Json;
using DrawAim.Infrastructure.History;

namespace DrawAim.Infrastructure.Storage;

public sealed record HistoryLoadResult(
    int SchemaVersion,
    IReadOnlyList<ExerciseHistoryEntry> Entries,
    StorageLoadStatus Status,
    string? RecoveryBackupPath = null,
    string? ErrorMessage = null);

/// <summary>Bounded, atomic JSON storage for exercise summary history.</summary>
public sealed class JsonExerciseHistoryStore
{
    private readonly DrawAimDataPaths _paths;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly int _maxEntries;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonExerciseHistoryStore(
        DrawAimDataPaths? paths = null,
        HistoryStoreOptions? options = null)
    {
        _paths = paths ?? DrawAimDataPaths.Resolve();
        _maxEntries = (options ?? new HistoryStoreOptions()).GetValidatedMaxEntries();
        _serializerOptions = JsonStorageInternals.CreateSerializerOptions();
    }

    public string FilePath => _paths.HistoryFilePath;

    public int MaxEntries => _maxEntries;

    public async Task<HistoryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(
        ExerciseHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HistoryLoadResult current = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfExistingHistoryWasUnavailable(current);

            var entries = new List<ExerciseHistoryEntry>(current.Entries.Count + 1);
            entries.AddRange(current.Entries.Select(ExerciseHistoryEntry.Normalize));
            entries.Add(ExerciseHistoryEntry.Normalize(entry));
            TrimOldest(entries);

            await SaveWithoutLockAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAsync(
        IEnumerable<ExerciseHistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<ExerciseHistoryEntry> snapshot = entries
            .Select(ExerciseHistoryEntry.Normalize)
            .ToList();
        TrimOldest(snapshot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveWithoutLockAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        ReplaceAsync([], cancellationToken);

    private async Task<HistoryLoadResult> LoadWithoutLockAsync(
        CancellationToken cancellationToken)
    {
        string path = _paths.HistoryFilePath;
        if (!File.Exists(path))
        {
            return CreateResult(
                ExerciseHistoryDocument.CurrentSchemaVersion,
                [],
                StorageLoadStatus.MissingUsedDefaults);
        }

        try
        {
            ExerciseHistoryDocument? document =
                await JsonStorageInternals.ReadAsync<ExerciseHistoryDocument>(
                    path,
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                throw new JsonException("The history document contains JSON null.");
            }

            List<ExerciseHistoryEntry> normalized = (document.Entries ?? [])
                .Where(static entry => entry is not null)
                .Select(ExerciseHistoryEntry.Normalize)
                .ToList();
            TrimOldest(normalized);

            return CreateResult(
                document.SchemaVersion <= 0
                    ? ExerciseHistoryDocument.CurrentSchemaVersion
                    : document.SchemaVersion,
                normalized,
                StorageLoadStatus.Loaded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return await RecoverCorruptAsync(exception, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            return await RecoverCorruptAsync(exception, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return Unavailable(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unavailable(exception);
        }
    }

    private async Task<HistoryLoadResult> RecoverCorruptAsync(
        Exception parseException,
        CancellationToken cancellationToken)
    {
        try
        {
            string backupPath = JsonStorageInternals.MoveCorruptFileToRecovery(
                _paths.HistoryFilePath,
                _paths.RecoveryDirectory);

            await SaveWithoutLockAsync([], cancellationToken).ConfigureAwait(false);

            return CreateResult(
                ExerciseHistoryDocument.CurrentSchemaVersion,
                [],
                StorageLoadStatus.CorruptRecovered,
                backupPath,
                parseException.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception recoveryException) when (
            recoveryException is IOException or UnauthorizedAccessException)
        {
            return CreateResult(
                ExerciseHistoryDocument.CurrentSchemaVersion,
                [],
                StorageLoadStatus.UnavailableUsedDefaults,
                errorMessage: $"{parseException.Message} Recovery failed: {recoveryException.Message}");
        }
    }

    private async Task SaveWithoutLockAsync(
        IReadOnlyCollection<ExerciseHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var document = new ExerciseHistoryDocument
        {
            SchemaVersion = ExerciseHistoryDocument.CurrentSchemaVersion,
            Entries = entries.Select(ExerciseHistoryEntry.Normalize).ToList(),
        };

        await JsonStorageInternals.WriteAtomicallyAsync(
            _paths.HistoryFilePath,
            document,
            _serializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private void TrimOldest(List<ExerciseHistoryEntry> entries)
    {
        int excess = entries.Count - _maxEntries;
        if (excess > 0)
        {
            entries.RemoveRange(0, excess);
        }
    }

    private void ThrowIfExistingHistoryWasUnavailable(HistoryLoadResult result)
    {
        if (result.Status == StorageLoadStatus.UnavailableUsedDefaults &&
            File.Exists(_paths.HistoryFilePath))
        {
            throw new IOException(
                "The existing history file could not be read; append was refused to avoid data loss.");
        }
    }

    private static HistoryLoadResult CreateResult(
        int schemaVersion,
        IEnumerable<ExerciseHistoryEntry> entries,
        StorageLoadStatus status,
        string? recoveryBackupPath = null,
        string? errorMessage = null)
    {
        var readOnlyEntries = new ReadOnlyCollection<ExerciseHistoryEntry>(entries.ToList());
        return new HistoryLoadResult(
            schemaVersion,
            readOnlyEntries,
            status,
            recoveryBackupPath,
            errorMessage);
    }

    private static HistoryLoadResult Unavailable(Exception exception) =>
        CreateResult(
            ExerciseHistoryDocument.CurrentSchemaVersion,
            [],
            StorageLoadStatus.UnavailableUsedDefaults,
            errorMessage: exception.Message);
}
