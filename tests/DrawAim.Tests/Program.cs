using System.Diagnostics;
using System.Text;

namespace DrawAim.Tests;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var tests = AllTests.Create().ToArray();
        var failed = 0;
        var suiteTimer = Stopwatch.StartNew();

        Console.WriteLine($"DrawAim 自动化测试：{tests.Length} 项\n");

        foreach (var test in tests)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                test.Body();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("通过");
                Console.ResetColor();
                Console.WriteLine($"  {test.Name} ({timer.Elapsed.TotalMilliseconds:F1} ms)");
            }
            catch (Exception exception)
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("失败");
                Console.ResetColor();
                Console.WriteLine($"  {test.Name}");
                Console.WriteLine($"      {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：{tests.Length - failed}/{tests.Length} 通过，用时 {suiteTimer.Elapsed.TotalSeconds:F2} 秒。");
        return failed == 0 ? 0 : 1;
    }
}
