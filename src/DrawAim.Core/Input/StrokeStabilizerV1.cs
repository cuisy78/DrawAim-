using DrawAim.Core.Geometry;

namespace DrawAim.Core.Input;

public sealed class StrokeStabilizerV1
{
    public const string Version = "StrokeStabilizerV1";
    public const double MaximumCoordinateMagnitude = 1_000_000;
    private const double MaximumTimestampMagnitude = 1_000_000_000_000;

    private bool _hasValue;
    private Point2 _previousRaw;
    private Point2 _previousStable;
    private double _previousTimestamp;
    private double _lastPositiveDelta = 1.0 / 120.0;

    public StrokeStabilizerV1(int level)
    {
        if (level is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        Level = level;
    }

    public int Level { get; }

    public double Strength => Level / 100.0;

    public double MaxLagDip => Level == 0
        ? 0
        : 1 + (31 * Math.Pow(Strength, 1.5));

    public void Reset()
    {
        _hasValue = false;
        _previousRaw = Point2.Zero;
        _previousStable = Point2.Zero;
        _previousTimestamp = 0;
        _lastPositiveDelta = 1.0 / 120.0;
    }

    public StrokeSample Process(StrokeSample sample)
    {
        if (!TryProcess(sample, out var output))
        {
            throw new ArgumentException("The first stroke sample must have a finite position.", nameof(sample));
        }

        return output;
    }

    public bool TryProcess(StrokeSample sample, out StrokeSample output)
    {
        if (!IsSafePosition(sample.Position))
        {
            output = default;
            return false;
        }

        var timestamp = NormalizeTimestamp(sample.TimestampSeconds);
        var pressure = NormalizePressure(sample.Pressure);
        if (!_hasValue)
        {
            _hasValue = true;
            _previousRaw = sample.Position;
            _previousStable = sample.Position;
            _previousTimestamp = timestamp;
            output = new StrokeSample(sample.Position, timestamp, pressure);
            return true;
        }

        var delta = timestamp - _previousTimestamp;
        if (!double.IsFinite(delta) || delta <= 0)
        {
            delta = _lastPositiveDelta;
            timestamp = _previousTimestamp + delta;
        }
        else
        {
            delta = Math.Clamp(delta, 1.0 / 4000.0, 0.1);
            _lastPositiveDelta = delta;
        }

        var stable = sample.Position;
        if (Level > 0)
        {
            var strength = Strength;
            var tauBase = 0.080 * strength * strength;
            var speed = Point2.Distance(sample.Position, _previousRaw) / delta;
            var tauEffective = tauBase /
                               (1 + (0.75 * Math.Min(speed / 1000.0, 2)));
            var alpha = tauEffective <= GeometryMath.Epsilon
                ? 1
                : 1 - Math.Exp(-delta / tauEffective);
            stable = _previousStable + ((sample.Position - _previousStable) * alpha);

            var lagVector = sample.Position - stable;
            var lag = lagVector.Length;
            var maximumLag = MaxLagDip;
            if (lag > maximumLag && lag > GeometryMath.Epsilon)
            {
                stable = sample.Position - ((lagVector / lag) * maximumLag);
            }
        }

        _previousRaw = sample.Position;
        _previousStable = stable;
        _previousTimestamp = timestamp;
        output = new StrokeSample(stable, timestamp, pressure);
        return true;
    }

    public static LogicalStroke Stabilize(
        IEnumerable<StrokeSample> samples,
        int level)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var stabilizer = new StrokeStabilizerV1(level);
        var output = new List<StrokeSample>();
        foreach (var sample in samples)
        {
            if (stabilizer.TryProcess(sample, out var stabilized))
            {
                output.Add(stabilized);
            }
        }

        return new LogicalStroke(output, level, Version);
    }

    private double NormalizeTimestamp(double timestamp)
    {
        if (double.IsFinite(timestamp) && Math.Abs(timestamp) <= MaximumTimestampMagnitude)
        {
            return timestamp;
        }

        return _hasValue
            ? _previousTimestamp + _lastPositiveDelta
            : 0;
    }

    private static double NormalizePressure(double pressure) =>
        double.IsFinite(pressure) ? Math.Clamp(pressure, 0, 1) : 1;

    private static bool IsSafePosition(Point2 position) =>
        position.IsFinite &&
        Math.Abs(position.X) <= MaximumCoordinateMagnitude &&
        Math.Abs(position.Y) <= MaximumCoordinateMagnitude;
}
