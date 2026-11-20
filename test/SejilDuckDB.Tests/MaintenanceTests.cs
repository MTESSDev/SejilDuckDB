using SejilDuckDB.Storage;
using static SejilDuckDB.Tests.Workspace;

namespace SejilDuckDB.Tests;

public class MaintenanceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static string Day(int daysAgo) => LogFileStore.DayKey(Now.AddDays(-daysAgo).DateTime);

    [Fact]
    public async Task Each_day_goes_to_its_own_file()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now, "today"), Event(Now.AddDays(-1), "yesterday"), Event(Now.AddDays(-1), "yesterday again"));

        Assert.Equal(new[] { $"log-{Day(1)}.jsonl", $"log-{Day(0)}.jsonl" }.Order(), w.Files());
    }

    [Fact]
    public async Task The_days_older_than_the_limit_are_compressed_and_still_searched()
    {
        using var w = new Workspace();
        await w.WriteAsync(
            Event(Now.AddDays(-10), "ten days ago", properties: ("UserName", "bob")),
            Event(Now.AddDays(-9), "nine days ago"),
            Event(Now.AddDays(-3), "three days ago"),
            Event(Now, "today"));

        var result = new LogMaintenance(w.Store, compressAfterDays: 7, retentionDays: 90).Run(Now.Date);

        Assert.Equal(2, result.Compressed);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, w.Files("log-*.jsonl.gz").Length);
        Assert.Equal(new[] { $"log-{Day(3)}.jsonl", $"log-{Day(0)}.jsonl" }.Order(), w.Files("log-*.jsonl"));
        Assert.Empty(w.Files("*.tmp"));

        var all = await w.SearchAsync();
        Assert.Equal(new[] { "today", "three days ago", "nine days ago", "ten days ago" }, all.Events.Select(e => e.Message));
        Assert.Equal(new[] { "ten days ago" }, (await w.SearchAsync("UserName = bob")).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task A_day_exactly_at_the_limit_is_kept_as_it_is()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddDays(-7), "seven"), Event(Now.AddDays(-8), "eight"));

        new LogMaintenance(w.Store, 7, 90).Run(Now.Date);

        Assert.Single(w.Files("log-*.jsonl"));
        Assert.Single(w.Files("log-*.jsonl.gz"));
    }

    [Fact]
    public async Task Compressing_twice_changes_nothing()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddDays(-10), "old"));
        var maintenance = new LogMaintenance(w.Store, 7, 90);

        maintenance.Run(Now.Date);
        var second = maintenance.Run(Now.Date);

        Assert.Equal(0, second.Compressed);
        Assert.Single(w.Files("log-*.jsonl.gz"));
        Assert.Single((await w.SearchAsync()).Events);
    }

    [Fact]
    public async Task The_days_past_the_retention_are_deleted_compressed_or_not()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddDays(-100), "ancient"), Event(Now.AddDays(-95), "very old"), Event(Now.AddDays(-20), "old"), Event(Now, "today"));
        new LogMaintenance(w.Store, 7, 1000).Run(Now.Date); // compress only: the two oldest are .gz now
        Assert.Equal(3, w.Files("log-*.jsonl.gz").Length);

        var result = new LogMaintenance(w.Store, 7, 90).Run(Now.Date);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(new[] { "today", "old" }, (await w.SearchAsync()).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Null_limits_keep_everything_for_ever()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddDays(-400), "ancient"));

        var result = new LogMaintenance(w.Store, null, null).Run(Now.Date);

        Assert.Equal(new MaintenanceResult(0, 0), result);
        Assert.Single(w.Files("log-*.jsonl"));
    }

    [Fact]
    public async Task A_late_event_for_a_day_already_compressed_gets_a_new_file_and_is_found_with_the_others()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddDays(-10).AddMinutes(-5), "first"));
        new LogMaintenance(w.Store, 7, 90).Run(Now.Date);

        await w.WriteAsync(Event(Now.AddDays(-10), "late")); // arrives long after

        Assert.Single(w.Files("log-*.jsonl.gz"));
        Assert.Single(w.Files("log-*.jsonl"));
        Assert.Equal(new[] { "late", "first" }, (await w.SearchAsync()).Events.Select(e => e.Message));

        // and the next maintenance folds it into a second compressed file of that day
        new LogMaintenance(w.Store, 7, 90).Run(Now.Date);
        Assert.Equal(2, w.Files("log-*.jsonl.gz").Length);
        Assert.Empty(w.Files("log-*.jsonl"));
        Assert.Equal(new[] { "late", "first" }, (await w.SearchAsync()).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task An_unfinished_temporary_file_is_never_read()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now, "real"));
        await File.WriteAllTextAsync(Path.Combine(w.Directory, $"log-{Day(0)}.20260101000000000.jsonl.gz.tmp"), "garbage");

        Assert.Equal(new[] { "real" }, (await w.SearchAsync()).Events.Select(e => e.Message));
    }

    [Fact]
    public void Only_log_files_are_recognised()
    {
        Assert.True(LogFileStore.TryParseDay("log-20261007.jsonl", out var d1));
        Assert.Equal(new DateTime(2026, 10, 7), d1);
        Assert.True(LogFileStore.TryParseDay("log-20261007.20261120031500123.jsonl.gz", out _));
        Assert.False(LogFileStore.TryParseDay("log-20261007.20261120031500123.jsonl.gz.tmp", out _));
        Assert.False(LogFileStore.TryParseDay("queries.json", out _));
        Assert.False(LogFileStore.TryParseDay("log-20261340.jsonl", out _));
        Assert.False(LogFileStore.TryParseDay("log-2026.jsonl", out _));
    }
}
