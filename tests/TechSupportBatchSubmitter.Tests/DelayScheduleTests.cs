using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Tests;

public sealed class DelayScheduleTests
{
    [Fact]
    public void Fixed_ReturnsSameDelayEveryTime()
    {
        var schedule = DelaySchedule.Fixed(TimeSpan.FromSeconds(12));

        Assert.True(schedule.IsFixed);
        Assert.Equal(TimeSpan.FromSeconds(12), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(12), schedule.NextDelay());
    }

    [Fact]
    public void NextDelay_StaysWithinConfiguredRange()
    {
        var schedule = new DelaySchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        for (var i = 0; i < 100; i++)
        {
            var actual = schedule.NextDelay();

            Assert.InRange(
                actual,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10));
        }
    }
}
