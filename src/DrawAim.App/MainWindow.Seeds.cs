using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace DrawAim.App;

public partial class MainWindow
{
    private ulong ResolveQuestionSeed(
        TextBox seedTextBox,
        CheckBox lockSeedControl,
        ulong fallback)
    {
        if (lockSeedControl.IsChecked == true)
        {
            ulong lockedSeed = ParseSeed(seedTextBox.Text, fallback);
            seedTextBox.Text = lockedSeed.ToString(CultureInfo.InvariantCulture);
            return lockedSeed;
        }

        ulong previousSeed = ParseSeed(seedTextBox.Text, 0);
        ulong seed;
        do
        {
            seed = CreateRandomSeed();
        }
        while (seed == previousSeed);

        seedTextBox.Text = seed.ToString(CultureInfo.InvariantCulture);
        return seed;
    }

    private static ulong CreateRandomSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong seed;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            seed = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        while (seed == 0);

        return seed;
    }

    private void SeedLock_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        if (ReferenceEquals(sender, Mode1LockSeed))
        {
            Mode1Instruction.Text = Mode1LockSeed.IsChecked == true
                ? "Seed 已锁定；后续题目按当前 Seed 和题号确定性生成。"
                : "Seed 已解锁；从下一题开始每题自动生成新 Seed。";
        }
        else if (ReferenceEquals(sender, Mode2LockSeed))
        {
            Mode2Status.Text = Mode2LockSeed.IsChecked == true
                ? "Seed 已锁定；组合序列可复现。"
                : "Seed 已解锁；从下一题开始每题自动换 Seed。";
        }
        else if (ReferenceEquals(sender, Mode3LockSeed))
        {
            Mode3SimilarityHint.Text = Mode3LockSeed.IsChecked == true
                ? "Seed 已锁定；目标颜色序列可复现。"
                : "Seed 已解锁；从下一题开始每题自动换 Seed。";
        }
    }
}
