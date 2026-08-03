namespace DrawAim.Tests;

internal static partial class AllTests
{
    public static IEnumerable<TestCase> Create()
    {
        foreach (var test in GeometryTests()) yield return test;
        foreach (var test in StabilizerTests()) yield return test;
        foreach (var test in GenerationTests()) yield return test;
        foreach (var test in LineScoreTests()) yield return test;
        foreach (var test in MultiLineScoreTests()) yield return test;
        foreach (var test in ColorTests()) yield return test;
        foreach (var test in InfrastructureTests()) yield return test;
    }

    private static partial IEnumerable<TestCase> GeometryTests();
    private static partial IEnumerable<TestCase> StabilizerTests();
    private static partial IEnumerable<TestCase> GenerationTests();
    private static partial IEnumerable<TestCase> LineScoreTests();
    private static partial IEnumerable<TestCase> MultiLineScoreTests();
    private static partial IEnumerable<TestCase> ColorTests();
    private static partial IEnumerable<TestCase> InfrastructureTests();
}
