using System.Text.Json;
using DrawAim.Infrastructure.Settings;

namespace DrawAim.Infrastructure.Storage;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    StorageLoadStatus Status,
    string? RecoveryBackupPath = null,
    string? ErrorMessage = null);

/// <summary>Thread-safe local settings persistence with corruption recovery.</summary>
public sealed class JsonSettingsStore
{
    private readonly DrawAimDataPaths _paths;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(DrawAimDataPaths? paths = null)
    {
        _paths = paths ?? DrawAimDataPaths.Resolve();
        _serializerOptions = JsonStorageInternals.CreateSerializerOptions();
    }

    public string FilePath => _paths.SettingsFilePath;

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings snapshot = AppSettings.Normalize(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonStorageInternals.WriteAtomicallyAsync(
                _paths.SettingsFilePath,
                snapshot,
                _serializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SettingsLoadResult> LoadWithoutLockAsync(
        CancellationToken cancellationToken)
    {
        string path = _paths.SettingsFilePath;
        if (!File.Exists(path))
        {
            return new SettingsLoadResult(
                AppSettings.CreateDefault(),
                StorageLoadStatus.MissingUsedDefaults);
        }

        try
        {
            AppSettings? settings = await JsonStorageInternals.ReadAsync<AppSettings>(
                path,
                _serializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (settings is null)
            {
                throw new JsonException("The settings document contains JSON null.");
            }

            return new SettingsLoadResult(
                AppSettings.Normalize(settings),
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

    private async Task<SettingsLoadResult> RecoverCorruptAsync(
        Exception parseException,
        CancellationToken cancellationToken)
    {
        AppSettings defaults = AppSettings.CreateDefault();

        try
        {
            string backupPath = JsonStorageInternals.MoveCorruptFileToRecovery(
                _paths.SettingsFilePath,
                _paths.RecoveryDirectory);

            await JsonStorageInternals.WriteAtomicallyAsync(
                _paths.SettingsFilePath,
                defaults,
                _serializerOptions,
                cancellationToken).ConfigureAwait(false);

            return new SettingsLoadResult(
                defaults,
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
            return new SettingsLoadResult(
                defaults,
                StorageLoadStatus.UnavailableUsedDefaults,
                ErrorMessage: $"{parseException.Message} Recovery failed: {recoveryException.Message}");
        }
    }

    private static SettingsLoadResult Unavailable(Exception exception) =>
        new(
            AppSettings.CreateDefault(),
            StorageLoadStatus.UnavailableUsedDefaults,
            ErrorMessage: exception.Message);
}
