namespace DrawAim.App.Services;

/// <summary>
/// A monotonically increasing identity for one answer snapshot. QuestionVersion
/// changes for each new exercise; AnswerVersion changes for each geometry update.
/// </summary>
public readonly record struct LatestWinsKey(
    long QuestionVersion,
    long AnswerVersion,
    long SettingsVersion)
{
    internal void Validate()
    {
        if (QuestionVersion < 0 || AnswerVersion < 0 || SettingsVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QuestionVersion),
                "Latest-wins versions cannot be negative.");
        }
    }
}

public enum LatestWinsStatus
{
    Published = 1,
    Superseded = 2,
    Cancelled = 3,
    Faulted = 4,
    RejectedStale = 5,
}

public sealed record LatestWinsPublishedResult<TResult>(
    LatestWinsKey Key,
    long RequestSequence,
    TResult Result,
    DateTimeOffset PublishedAtUtc);

public sealed record LatestWinsExecutionResult<TResult>(
    LatestWinsStatus Status,
    LatestWinsKey Key,
    long RequestSequence,
    LatestWinsPublishedResult<TResult>? PublishedResult = null,
    Exception? Error = null);

/// <summary>
/// Runs at most the newest snapshot, throttles evaluation starts, cancels prior
/// requests and publishes results only after the complete version key is rechecked.
/// The authoritative <see cref="LatestResult"/> can never be replaced by an older request.
/// </summary>
public sealed class LatestWinsCoordinator<TSnapshot, TResult> : IDisposable
{
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly Func<TSnapshot, CancellationToken, ValueTask<TResult>> _worker;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumInterval;
    private CancellationTokenSource? _activeCancellation;
    private LatestWinsKey? _currentKey;
    private LatestWinsPublishedResult<TResult>? _latestResult;
    private long _requestSequence;
    private long _lastStartTimestamp;
    private bool _hasStartedWork;
    private bool _disposed;

    public LatestWinsCoordinator(
        Func<TSnapshot, CancellationToken, ValueTask<TResult>> worker,
        TimeSpan? minimumInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        TimeSpan interval = minimumInterval ?? DefaultMinimumInterval;
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        _worker = worker;
        _minimumInterval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan MinimumInterval => _minimumInterval;

    public LatestWinsKey? CurrentKey
    {
        get
        {
            lock (_sync)
            {
                return _currentKey;
            }
        }
    }

    public LatestWinsPublishedResult<TResult>? LatestResult
    {
        get
        {
            lock (_sync)
            {
                return _latestResult;
            }
        }
    }

    public Task<LatestWinsExecutionResult<TResult>> SubmitAsync(
        LatestWinsKey key,
        TSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        key.Validate();
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource requestCancellation;
        long sequence;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_currentKey is LatestWinsKey current && IsStale(key, current))
            {
                return Task.FromResult(
                    new LatestWinsExecutionResult<TResult>(
                        LatestWinsStatus.RejectedStale,
                        key,
                        _requestSequence));
            }

            sequence = checked(++_requestSequence);
            previousCancellation = _activeCancellation;
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = requestCancellation;
            _currentKey = key;
            _latestResult = null;
        }

        CancelWithoutThrowing(previousCancellation);
        return ExecuteAsync(sequence, key, snapshot, requestCancellation);
    }

    public bool IsCurrent(LatestWinsKey key)
    {
        lock (_sync)
        {
            return !_disposed && _currentKey == key;
        }
    }

    public bool TryGetLatest(out LatestWinsPublishedResult<TResult>? result)
    {
        lock (_sync)
        {
            result = _latestResult;
            return result is not null;
        }
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            checked
            {
                _requestSequence++;
            }

            cancellation = _activeCancellation;
            _activeCancellation = null;
            _currentKey = null;
            _latestResult = null;
        }

        CancelWithoutThrowing(cancellation);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _activeCancellation;
            _activeCancellation = null;
            _currentKey = null;
            _latestResult = null;
        }

        CancelWithoutThrowing(cancellation);
    }

    private async Task<LatestWinsExecutionResult<TResult>> ExecuteAsync(
        long sequence,
        LatestWinsKey key,
        TSnapshot snapshot,
        CancellationTokenSource requestCancellation)
    {
        CancellationToken cancellationToken = requestCancellation.Token;

        try
        {
            TimeSpan delay = GetThrottleDelay();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            if (!TryMarkWorkStarted(sequence, key))
            {
                return Superseded(sequence, key);
            }

            TResult result = await _worker(snapshot, cancellationToken).ConfigureAwait(false);
            LatestWinsPublishedResult<TResult>? published;

            lock (_sync)
            {
                if (!IsCurrentWithoutLock(sequence, key))
                {
                    return Superseded(sequence, key);
                }

                published = new LatestWinsPublishedResult<TResult>(
                    key,
                    sequence,
                    result,
                    _timeProvider.GetUtcNow());
                _latestResult = published;
            }

            return new LatestWinsExecutionResult<TResult>(
                LatestWinsStatus.Published,
                key,
                sequence,
                published);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                return IsCurrentWithoutLock(sequence, key)
                    ? new LatestWinsExecutionResult<TResult>(LatestWinsStatus.Cancelled, key, sequence)
                    : Superseded(sequence, key);
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                return IsCurrentWithoutLock(sequence, key)
                    ? new LatestWinsExecutionResult<TResult>(
                        LatestWinsStatus.Faulted,
                        key,
                        sequence,
                        Error: exception)
                    : Superseded(sequence, key);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeCancellation, requestCancellation))
                {
                    _activeCancellation = null;
                }
            }

            requestCancellation.Dispose();
        }
    }

    private TimeSpan GetThrottleDelay()
    {
        lock (_sync)
        {
            if (!_hasStartedWork || _minimumInterval == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastStartTimestamp);
            return elapsed >= _minimumInterval
                ? TimeSpan.Zero
                : _minimumInterval - elapsed;
        }
    }

    private bool TryMarkWorkStarted(long sequence, LatestWinsKey key)
    {
        lock (_sync)
        {
            if (!IsCurrentWithoutLock(sequence, key))
            {
                return false;
            }

            _lastStartTimestamp = _timeProvider.GetTimestamp();
            _hasStartedWork = true;
            return true;
        }
    }

    private bool IsCurrentWithoutLock(long sequence, LatestWinsKey key) =>
        !_disposed && _requestSequence == sequence && _currentKey == key;

    private static bool IsStale(LatestWinsKey candidate, LatestWinsKey current)
    {
        if (candidate.QuestionVersion != current.QuestionVersion)
        {
            return candidate.QuestionVersion < current.QuestionVersion;
        }

        if (candidate.AnswerVersion != current.AnswerVersion)
        {
            return candidate.AnswerVersion < current.AnswerVersion;
        }

        return candidate.SettingsVersion < current.SettingsVersion;
    }

    private static LatestWinsExecutionResult<TResult> Superseded(
        long sequence,
        LatestWinsKey key) =>
        new(LatestWinsStatus.Superseded, key, sequence);

    private static void CancelWithoutThrowing(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request completed between replacement and cancellation.
        }
    }
}
