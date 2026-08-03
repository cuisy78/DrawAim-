namespace DrawAim.Core.Randomness;

public struct Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;
    private ulong _state;
    private readonly ulong _increment;

    public Pcg32(ulong seed, ulong sequence = 1442695040888963407UL)
    {
        _state = 0;
        _increment = (sequence << 1) | 1;
        _ = NextUInt32();
        _state += seed;
        _ = NextUInt32();
    }

    public uint NextUInt32()
    {
        var oldState = _state;
        _state = unchecked((oldState * Multiplier) + _increment);
        var xorShifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
        var rotation = (int)(oldState >> 59);
        return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
    }

    public ulong NextUInt64() => ((ulong)NextUInt32() << 32) | NextUInt32();

    public double NextDouble()
    {
        var high = (ulong)(NextUInt32() >> 5);
        var low = (ulong)(NextUInt32() >> 6);
        return ((high << 26) + low) / 9007199254740992.0;
    }

    public double NextDouble(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        return minimum + ((maximum - minimum) * NextDouble());
    }

    public int NextInt32(int maximumExclusive)
    {
        if (maximumExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExclusive));
        }

        var bound = (uint)maximumExclusive;
        var threshold = unchecked((uint)(0 - bound)) % bound;
        while (true)
        {
            var value = NextUInt32();
            if (value >= threshold)
            {
                return (int)(value % bound);
            }
        }
    }

    public bool NextBoolean() => (NextUInt32() & 1) == 1;
}
