using System.Text;

namespace DrawAim.Infrastructure.Logging;

public enum DrawAimLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

public sealed class RollingFileLoggerOptions
{
    public string FileNamePrefix { get; init; } = "drawaim";

    public long MaxFileBytes { get; init; } = 1_048_576;

    public int MaxRetainedFiles { get; init; } = 5;

    public int MaxEntryCharacters { get; init; } = 16_384;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(FileNamePrefix) ||
            FileNamePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("FileNamePrefix must be a valid file name prefix.", nameof(FileNamePrefix));
        }

        if (MaxFileBytes is < 65_536 or > 1_073_741_824)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
        }

        if (MaxRetainedFiles is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedFiles));
        }

        if (MaxEntryCharacters is < 256 or > 262_144 || MaxEntryCharacters * 4L > MaxFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntryCharacters));
        }
    }
}

/// <summary>
/// A dependency-free, bounded text logger. Expected I/O failures return false
/// instead of crashing the training application.
/// </summary>
public sealed class RollingFileLogger
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly RollingFileLoggerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentFilePath;

    public RollingFileLogger(
        Storage.DrawAimDataPaths? paths = null,
        RollingFileLoggerOptions? options = null)
    {
        _directory = (paths ?? Storage.DrawAimDataPaths.Resolve()).LogsDirectory;
        _options = options ?? new RollingFileLoggerOptions();
        _options.Validate();
    }

    public string DirectoryPath => _directory;

    public async ValueTask<bool> WriteAsync(
        DrawAimLogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string entry = FormatEntry(level, message, exception);
            byte[] bytes = Utf8WithoutBom.GetBytes(entry);
            Directory.CreateDirectory(_directory);

            string path = SelectCurrentFile(bytes.Length);
            await using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            PruneOldFiles(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exceptionToIgnore) when (
            exceptionToIgnore is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string SelectCurrentFile(int incomingByteCount)
    {
        if (_currentFilePath is not null && File.Exists(_currentFilePath))
        {
            long length = new FileInfo(_currentFilePath).Length;
            if (length + incomingByteCount <= _options.MaxFileBytes)
            {
                return _currentFilePath;
            }
        }

        _currentFilePath = Path.Combine(
            _directory,
            $"{_options.FileNamePrefix}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.log");
        return _currentFilePath;
    }

    private string FormatEntry(DrawAimLogLevel level, string message, Exception? exception)
    {
        string sanitizedMessage = Sanitize(message);
        string entry = exception is null
            ? $"{DateTimeOffset.UtcNow:O} [{level}] {sanitizedMessage}{Environment.NewLine}"
            : $"{DateTimeOffset.UtcNow:O} [{level}] {sanitizedMessage} | {Sanitize(exception.ToString())}{Environment.NewLine}";

        return entry.Length <= _options.MaxEntryCharacters
            ? entry
            : string.Concat(entry.AsSpan(0, _options.MaxEntryCharacters - 2), "…", Environment.NewLine);
    }

    private static string Sanitize(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private void PruneOldFiles(string currentPath)
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(_directory)
                .GetFiles($"{_options.FileNamePrefix}-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ThenByDescending(static file => file.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (FileInfo file in files.Skip(_options.MaxRetainedFiles))
        {
            if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                file.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging remains best effort; a locked old file can be pruned later.
            }
        }
    }
}
