using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DrawAim.Core.Generation;

namespace DrawAim.Core.Randomness;

public static class SeedDerivation
{
    public static ulong Derive(GenerationKey key, params long[] additionalValues)
    {
        ArgumentNullException.ThrowIfNull(additionalValues);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(writer, key.GeneratorVersion);
            writer.Write((int)key.Mode);
            writer.Write(key.BaseSeed);
            writer.Write(key.ExerciseIndex);
            WriteString(writer, key.SettingsFingerprint);
            writer.Write(Quantize(key.CanvasWidthDip));
            writer.Write(Quantize(key.CanvasHeightDip));
            foreach (var value in additionalValues)
            {
                writer.Write(value);
            }
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)), hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    public static long Quantize(double value, double scale = 1_000_000)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }

        var scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
        return scaled >= long.MaxValue
            ? long.MaxValue
            : scaled <= long.MinValue
                ? long.MinValue
                : (long)scaled;
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
