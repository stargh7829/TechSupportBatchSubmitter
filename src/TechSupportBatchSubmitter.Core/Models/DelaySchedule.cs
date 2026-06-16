namespace TechSupportBatchSubmitter.Core.Models;

public sealed class DelaySchedule
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private readonly Random _random;
    private readonly object _sync = new();

    public DelaySchedule(TimeSpan minimum, TimeSpan maximum)
        : this(minimum, maximum, Random.Shared)
    {
    }

    internal DelaySchedule(TimeSpan minimum, TimeSpan maximum, Random random)
    {
        if (minimum < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum));
        }

        if (maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        _minimum = minimum;
        _maximum = maximum;
        _random = random;
    }

    public TimeSpan Minimum => _minimum;
    public TimeSpan Maximum => _maximum;
    public bool IsFixed => _minimum == _maximum;

    public static DelaySchedule Fixed(TimeSpan interval) => new(interval, interval);

    public TimeSpan NextDelay()
    {
        if (IsFixed)
        {
            return _minimum;
        }

        var minMilliseconds = (long)Math.Ceiling(_minimum.TotalMilliseconds);
        var maxMilliseconds = (long)Math.Ceiling(_maximum.TotalMilliseconds);
        lock (_sync)
        {
            return TimeSpan.FromMilliseconds(_random.NextInt64(minMilliseconds, maxMilliseconds + 1));
        }
    }
}
