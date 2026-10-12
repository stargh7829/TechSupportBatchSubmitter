using System.Text.Json;
using TechSupportBatchSubmitter.Wpf.Services;

namespace TechSupportBatchSubmitter.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void LoadOrCreate_NewSettingsUseThreeToTenMinutes()
    {
        WithSettingsPath(path =>
        {
            var settings = AppSettings.LoadOrCreate(path);
            var schedule = settings.CreateSubmissionDelaySchedule();

            Assert.Equal(TimeSpan.FromMinutes(3), schedule.Minimum);
            Assert.Equal(TimeSpan.FromMinutes(10), schedule.Maximum);
            Assert.False(schedule.IsFixed);
            Assert.Equal(180, AppSettings.LoadOrCreate(path).DefaultSubmissionDelayMinSeconds);
            Assert.Equal(600, AppSettings.LoadOrCreate(path).DefaultSubmissionDelayMaxSeconds);
            Assert.Equal(TimeSpan.FromSeconds(5), settings.CreateCloseDelaySchedule().Minimum);
            Assert.Equal(TimeSpan.FromSeconds(10), settings.CreateCloseDelaySchedule().Maximum);
        });
    }

    [Theory]
    [InlineData(30, 90, 180, 600)]
    [InlineData(120, 240, 120, 240)]
    [InlineData(45, 45, 45, 45)]
    public void LoadOrCreate_UpdatesLegacyDefaultAndPreservesCustomSettings(
        int minimum, int maximum, int expectedMinimum, int expectedMaximum)
    {
        WithSettingsPath(path =>
        {
            var original = new AppSettings
            {
                WorkbenchUrl = "https://example.test/workbench",
                SupportPlatformUrl = "https://example.test/support",
                DefaultSubmissionDelayMinSeconds = minimum,
                DefaultSubmissionDelayMaxSeconds = maximum,
                DefaultCloseDelayMinSeconds = 7,
                DefaultCloseDelayMaxSeconds = 12
            };
            File.WriteAllText(path, JsonSerializer.Serialize(original));

            AppSettings.LoadOrCreate(path);
            var persisted = AppSettings.LoadOrCreate(path);
            var schedule = persisted.CreateSubmissionDelaySchedule();

            Assert.Equal(TimeSpan.FromSeconds(expectedMinimum), schedule.Minimum);
            Assert.Equal(TimeSpan.FromSeconds(expectedMaximum), schedule.Maximum);
            Assert.Equal(original.WorkbenchUrl, persisted.WorkbenchUrl);
            Assert.Equal(original.SupportPlatformUrl, persisted.SupportPlatformUrl);
            Assert.Equal(7, persisted.DefaultCloseDelayMinSeconds);
            Assert.Equal(12, persisted.DefaultCloseDelayMaxSeconds);
        });
    }

    private static void WithSettingsPath(Action<string> test)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            test(path);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
