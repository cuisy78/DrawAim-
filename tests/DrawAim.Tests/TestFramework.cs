namespace DrawAim.Tests;

internal sealed record TestCase(string Name, Action Body);

internal static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"期望 {expected}，实际 {actual}。");
        }
    }

    public static void Near(double expected, double actual, double tolerance, string? message = null)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                message ?? $"期望 {expected:G8} ± {tolerance:G4}，实际 {actual:G8}。");
        }
    }

    public static void InRange(double actual, double minimum, double maximum, string? message = null)
    {
        if (!double.IsFinite(actual) || actual < minimum || actual > maximum)
        {
            throw new InvalidOperationException(
                message ?? $"期望 [{minimum:G8}, {maximum:G8}]，实际 {actual:G8}。");
        }
    }

    public static T Throws<T>(Action action, string? message = null)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message ?? $"期望抛出 {typeof(T).Name}。");
    }
}
