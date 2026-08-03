namespace DrawAim.Core.Geometry;

public readonly record struct Point2(double X, double Y)
{
    public static Point2 Zero => new(0, 0);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public double LengthSquared => (X * X) + (Y * Y);

    public Point2 Normalized()
    {
        var length = Length;
        return length > GeometryMath.Epsilon && double.IsFinite(length)
            ? this / length
            : Zero;
    }

    public static double Dot(Point2 left, Point2 right) =>
        (left.X * right.X) + (left.Y * right.Y);

    public static double Cross(Point2 left, Point2 right) =>
        (left.X * right.Y) - (left.Y * right.X);

    public static double Distance(Point2 left, Point2 right) => (left - right).Length;

    public static Point2 Lerp(Point2 start, Point2 end, double amount) =>
        start + ((end - start) * amount);

    public static Point2 operator +(Point2 left, Point2 right) =>
        new(left.X + right.X, left.Y + right.Y);

    public static Point2 operator -(Point2 left, Point2 right) =>
        new(left.X - right.X, left.Y - right.Y);

    public static Point2 operator -(Point2 value) => new(-value.X, -value.Y);

    public static Point2 operator *(Point2 value, double scale) =>
        new(value.X * scale, value.Y * scale);

    public static Point2 operator *(double scale, Point2 value) => value * scale;

    public static Point2 operator /(Point2 value, double divisor) =>
        new(value.X / divisor, value.Y / divisor);
}

public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsFinite =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height);

    public bool Contains(Point2 point, double epsilon = 0) =>
        point.X >= Left - epsilon && point.X <= Right + epsilon &&
        point.Y >= Top - epsilon && point.Y <= Bottom + epsilon;
}
