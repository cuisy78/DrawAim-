using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrawAim.Infrastructure.Storage;

internal static class JsonStorageInternals
{
    internal static JsonSerializerOptions CreateSerializerOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            },
        };

    internal static async Task<T?> ReadAsync<T>(
        string path,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            serializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAtomicallyAsync<T>(
        string path,
        T value,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The destination must include a directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                }))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    serializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    internal static string MoveCorruptFileToRecovery(string path, string recoveryDirectory)
    {
        Directory.CreateDirectory(recoveryDirectory);

        string baseName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string backupName = $"{baseName}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}{extension}";
        string backupPath = Path.Combine(recoveryDirectory, backupName);

        File.Move(path, backupPath);
        return backupPath;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale, uniquely named temporary file is safer than risking the destination.
        }
        catch (UnauthorizedAccessException)
        {
            // The original error remains the actionable failure.
        }
    }
}
