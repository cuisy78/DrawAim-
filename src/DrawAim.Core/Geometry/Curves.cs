using System.Collections.ObjectModel;

namespace DrawAim.Core.Geometry;

public enum CurveKind
{
    Straight,
    CShape,
    SShape,
}

public readonly record struct CubicBezier2(
    Point2 P0,
    Point2 P1,
    Point2 P2,
    Point2 P3)
{
    public Point2 Evaluate(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var u = 1 - t;
        return (u * u * u * P0) +
               (3 * u * u * t * P1) +
               (3 * u * t * t * P2) +
               (t * t * t * P3);
    }

    public Point2 Derivative(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var u = 1 - t;
        return (3 * u * u * (P1 - P0)) +
               (6 * u * t * (P2 - P1)) +
               (3 * t * t * (P3 - P2));
    }

    public Point2 SecondDerivative(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (6 * (1 - t) * (P2 - (2 * P1) + P0)) +
               (6 * t * (P3 - (2 * P2) + P1));
    }

    public CubicBezier2 Transform(double angleRadians, Point2 translation, double scale = 1)
    {
        var cosine = Math.Cos(angleRadians);
        var sine = Math.Sin(angleRadians);

        Point2 TransformPoint(Point2 point) => new(
            ((point.X * cosine) - (point.Y * sine)) * scale + translation.X,
            ((point.X * sine) + (point.Y * cosine)) * scale + translation.Y);

        return new CubicBezier2(
            TransformPoint(P0),
            TransformPoint(P1),
            TransformPoint(P2),
            TransformPoint(P3));
    }
}

public sealed class TargetCurve
{
    private readonly ReadOnlyCollection<Point2> _polyline;

    public TargetCurve(
        CurveKind kind,
        CubicBezier2 bezier,
        double flatteningTolerance = 0.25,
        bool suggestedForward = true)
    {
        if (!bezier.P0.IsFinite || !bezier.P1.IsFinite ||
            !bezier.P2.IsFinite || !bezier.P3.IsFinite)
        {
            throw new ArgumentException("Bezier control points must be finite.", nameof(bezier));
        }

        if (!double.IsFinite(flatteningTolerance) || flatteningTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flatteningTolerance));
        }

        Kind = kind;
        Bezier = bezier;
        FlatteningTolerance = flatteningTolerance;
        SuggestedForward = suggestedForward;
        _polyline = Array.AsReadOnly(GeometryMath.FlattenBezier(bezier, flatteningTolerance).ToArray());
    }

    public CurveKind Kind { get; }

    public CubicBezier2 Bezier { get; }

    public double FlatteningTolerance { get; }

    public bool SuggestedForward { get; }

    public Point2 SuggestedStart => SuggestedForward ? Bezier.P0 : Bezier.P3;

    public IReadOnlyList<Point2> Polyline => _polyline;

    public double Length => GeometryMath.PolylineLength(_polyline);

    public Rect2 Bounds => GeometryMath.BezierBounds(Bezier);
}
