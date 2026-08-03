using System.Collections.ObjectModel;

namespace DrawAim.Core.Geometry;

public readonly record struct StrokeSample(
    Point2 Position,
    double TimestampSeconds,
    double Pressure = 1)
{
    public bool IsFinite =>
        Position.IsFinite &&
        double.IsFinite(TimestampSeconds) &&
        double.IsFinite(Pressure);
}

public sealed class LogicalStroke
{
    private readonly ReadOnlyCollection<StrokeSample> _samples;
    private readonly ReadOnlyCollection<Point2> _positions;

    public LogicalStroke(
        IEnumerable<StrokeSample> samples,
        int stabilizerLevel = 0,
        string stabilizerVersion = "StrokeStabilizerV1")
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegative(stabilizerLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stabilizerLevel, 100);

        StabilizerVersion = string.IsNullOrWhiteSpace(stabilizerVersion)
            ? throw new ArgumentException("Stabilizer version is required.", nameof(stabilizerVersion))
            : stabilizerVersion;
        StabilizerLevel = stabilizerLevel;

        var cleaned = new List<StrokeSample>();
        var lastTimestamp = -1.0;
        Point2? lastPosition = null;

        foreach (var sample in samples)
        {
            if (!sample.Position.IsFinite)
            {
                continue;
            }

            var timestamp = double.IsFinite(sample.TimestampSeconds)
                ? sample.TimestampSeconds
                : lastTimestamp + (1.0 / 1000.0);
            if (timestamp <= lastTimestamp)
            {
                timestamp = lastTimestamp + (1.0 / 1000.0);
            }

            var pressure = double.IsFinite(sample.Pressure)
                ? Math.Clamp(sample.Pressure, 0, 1)
                : 1;

            if (lastPosition is Point2 previous &&
                Point2.Distance(previous, sample.Position) <= GeometryMath.Epsilon)
            {
                lastTimestamp = timestamp;
                continue;
            }

            cleaned.Add(new StrokeSample(sample.Position, timestamp, pressure));
            lastPosition = sample.Position;
            lastTimestamp = timestamp;
        }

        _samples = Array.AsReadOnly(cleaned.ToArray());
        _positions = Array.AsReadOnly(cleaned.Select(static sample => sample.Position).ToArray());
    }

    public IReadOnlyList<StrokeSample> Samples => _samples;

    public int Count => _samples.Count;

    public int StabilizerLevel { get; }

    public string StabilizerVersion { get; }

    public IReadOnlyList<Point2> Positions => _positions;

    public static LogicalStroke FromPoints(
        IEnumerable<Point2> points,
        double sampleIntervalSeconds = 1.0 / 120.0,
        int stabilizerLevel = 0,
        string stabilizerVersion = "StrokeStabilizerV1")
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(sampleIntervalSeconds) || sampleIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIntervalSeconds));
        }

        var index = 0;
        return new LogicalStroke(
            points.Select(point =>
                new StrokeSample(point, index++ * sampleIntervalSeconds, 1)),
            stabilizerLevel,
            stabilizerVersion);
    }
}
