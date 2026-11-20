using SejilDuckDB.Query;
using static SejilDuckDB.Tests.Workspace;

namespace SejilDuckDB.Tests;

public class SearchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    [Fact]
    public async Task Events_come_back_newest_first_whatever_the_order_they_arrived_in()
    {
        using var w = new Workspace();
        await w.WriteAsync(
            Event(Now.AddMinutes(-1), "recent"),
            Event(Now.AddMinutes(-30), "old"),
            Event(Now.AddMinutes(-10), "middle"));

        var page = await w.SearchAsync();

        Assert.Equal(new[] { "recent", "middle", "old" }, page.Events.Select(e => e.Message));
        Assert.True(page.Exhausted);
        Assert.Null(page.Resume);
        Assert.All(page.Events, e => Assert.Equal("node-test", e.Node));
    }

    [Fact]
    public async Task An_event_keeps_everything_it_was_sent_with()
    {
        using var w = new Workspace();
        var at = new DateTimeOffset(2026, 10, 7, 19, 21, 41, 83, TimeSpan.Zero).AddTicks(1230); // 41.0830123
        await w.WriteAsync("Billing", Event(at, "Échec du paiement — « test »", level: 4, exception: "System.Exception: boom\n   at X",
            ("UserName", "bob"), ("Amount", "12.5"), ("Missing", null)));

        var e = Assert.Single((await w.SearchAsync()).Events);

        Assert.Equal("Échec du paiement — « test »", e.Message);
        Assert.Equal("Billing", e.SourceApp);
        Assert.Equal("Error", e.Level);
        Assert.Equal("System.Exception: boom\n   at X", e.Exception);
        Assert.Equal("2026-10-07T19:21:41.083123", e.Utc); // to the microsecond
        Assert.Equal(new[] { "Amount", "Missing", "UserName" }, e.Properties.Select(p => p.Name));
        Assert.Equal("12.5", e.Properties.Single(p => p.Name == "Amount").Value);
        Assert.Null(e.Properties.Single(p => p.Name == "Missing").Value);
        Assert.Equal(at.ToLocalTime().DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff"), e.Timestamp);
    }

    [Fact]
    public async Task A_word_is_searched_in_the_message_the_exception_and_the_properties()
    {
        using var w = new Workspace();
        await w.WriteAsync(
            Event(Now.AddMinutes(-1), "in the message: needle"),
            Event(Now.AddMinutes(-2), "x", exception: "System.Exception: NEEDLE"),
            Event(Now.AddMinutes(-3), "x", properties: ("Path", "/a/needle/b")),
            Event(Now.AddMinutes(-4), "nothing here"));

        var page = await w.SearchAsync("needle");

        Assert.Equal(3, page.Events.Count); // case does not matter
        Assert.DoesNotContain(page.Events, e => e.Message == "nothing here");
    }

    [Fact]
    public async Task A_property_can_be_compared_and_a_missing_one_never_equals()
    {
        using var w = new Workspace();
        await w.WriteAsync(
            Event(Now.AddMinutes(-1), "bob", properties: ("UserName", "bob")),
            Event(Now.AddMinutes(-2), "alice", properties: ("UserName", "alice")),
            Event(Now.AddMinutes(-3), "nobody"));

        Assert.Equal(new[] { "bob" }, (await w.SearchAsync("UserName = bob")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "bob" }, (await w.SearchAsync("UserName = 'BOB'")).Events.Select(e => e.Message)); // not case sensitive
        Assert.Equal(new[] { "bob", "alice" }, (await w.SearchAsync("UserName like '%'")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "alice" }, (await w.SearchAsync("UserName like 'al%'")).Events.Select(e => e.Message));
        // like SejilSQL: "not equal" also matches the events that do not have the property
        Assert.Equal(new[] { "alice", "nobody" }, (await w.SearchAsync("UserName != bob")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "nobody" }, (await w.SearchAsync("UserName not like '%'")).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Columns_can_be_queried_by_name_and_the_level_by_its_name_or_number()
    {
        using var w = new Workspace();
        await w.WriteAsync("Alpha", Event(Now.AddMinutes(-1), "a-info", level: 2));
        await w.WriteAsync("Beta", Event(Now.AddMinutes(-2), "b-error", level: 4, exception: "boom"));

        Assert.Equal(new[] { "b-error" }, (await w.SearchAsync("level = Error")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "b-error" }, (await w.SearchAsync("level = 4")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "a-info" }, (await w.SearchAsync("level != Error")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "a-info" }, (await w.SearchAsync("sourceApp = alpha")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "b-error" }, (await w.SearchAsync("message like '%error'")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "b-error" }, (await w.SearchAsync("exception = boom")).Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Conditions_combine_with_and_or_and_parentheses()
    {
        using var w = new Workspace();
        await w.WriteAsync(
            Event(Now.AddMinutes(-1), "one", level: 4, properties: ("App", "OIM")),
            Event(Now.AddMinutes(-2), "two", level: 2, properties: ("App", "OIM")),
            Event(Now.AddMinutes(-3), "three", level: 4, properties: ("App", "ECS")),
            Event(Now.AddMinutes(-4), "four", level: 2, properties: ("App", "ECS")));

        Assert.Equal(new[] { "one" }, (await w.SearchAsync("App = OIM and level = Error")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "one", "two", "three" }, (await w.SearchAsync("App = OIM || level = Error")).Events.Select(e => e.Message));
        Assert.Equal(new[] { "one", "three" }, (await w.SearchAsync("(App = OIM or App = ECS) && level = Error")).Events.Select(e => e.Message));
        // "or" inside a word is not an operator
        Assert.Empty((await w.SearchAsync("formulaire")).Events);
    }

    [Fact]
    public async Task The_filters_of_the_viewer_apply()
    {
        using var w = new Workspace();
        await w.WriteAsync(
            Event(Now.AddMinutes(-10), "recent error", level: 4, exception: "boom"),
            Event(Now.AddMinutes(-20), "recent info", level: 2),
            Event(Now.AddHours(-3), "older error", level: 4, exception: "boom"));

        async Task<string[]> Messages(LogQueryFilter f) => (await w.Reader.QueryAsync(f, null, 100)).Events.Select(e => e.Message!).ToArray();

        Assert.Equal(new[] { "recent error", "recent info" }, await Messages(new() { DateFilter = "1h" }));
        Assert.Equal(new[] { "recent error", "older error" }, await Messages(new() { LevelFilter = "Error" }));
        Assert.Equal(new[] { "recent error", "older error" }, await Messages(new() { ExceptionsOnly = true }));
        Assert.Equal(new[] { "recent error" }, await Messages(new() { DateFilter = "1h", LevelFilter = "Error", ExceptionsOnly = true }));

        var today = Now.Date;
        Assert.Contains("recent info", await Messages(new() { DateRangeFilter = new() { today, today.AddDays(1) } }));
        Assert.Empty(await Messages(new() { DateRangeFilter = new() { today.AddDays(-30), today.AddDays(-29) } }));
    }

    [Fact]
    public async Task Pages_follow_each_other_across_days_with_no_gap_and_no_duplicate()
    {
        using var w = new Workspace();
        var events = new List<SejilDuckDB.Storage.LogEvent>();
        for (var day = 0; day < 3; day++)
        {
            for (var n = 0; n < 90; n++)
            {
                events.Add(Event(Now.AddDays(-day).AddSeconds(-n), $"d{day}-n{n}"));
            }
        }

        await w.WriteAsync(events.ToArray());

        var seen = new List<string>();
        LogCursor? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = await w.SearchAsync(before: cursor, pageSize: 100);
            seen.AddRange(page.Events.Select(e => e.Message!));
            pages++;
            if (page.Exhausted) break;
            Assert.NotNull(page.Resume);
            cursor = page.Resume;
            Assert.True(pages < 10, "the paging does not end");
        }

        Assert.Equal(270, seen.Count);
        Assert.Equal(270, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(m => events.First(e => e.Message == m).Timestamp).ToList(), seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task A_page_that_holds_exactly_what_is_left_says_so()
    {
        using var w = new Workspace();
        await w.WriteAsync(Enumerable.Range(0, 5).Select(n => Event(Now.AddSeconds(-n), $"e{n}")).ToArray());

        var page = await w.SearchAsync(pageSize: 5);

        Assert.Equal(5, page.Events.Count);
        Assert.True(page.Exhausted);
        Assert.Null(page.Resume);

        var short5 = await w.SearchAsync(pageSize: 4);
        Assert.False(short5.Exhausted);
        Assert.NotNull(short5.Resume);
        Assert.Single((await w.SearchAsync(before: short5.Resume, pageSize: 4)).Events);
    }

    [Fact]
    public async Task Events_with_the_same_time_are_not_lost_between_two_pages()
    {
        using var w = new Workspace();
        var same = Now.AddMinutes(-5);
        await w.WriteAsync(Enumerable.Range(0, 7).Select(n => Event(same, $"tie{n}")).ToArray());

        var first = await w.SearchAsync(pageSize: 3);
        var second = await w.SearchAsync(before: first.Resume, pageSize: 3);
        var third = await w.SearchAsync(before: second.Resume, pageSize: 3);

        var all = first.Events.Concat(second.Events).Concat(third.Events).Select(e => e.Message).ToList();
        Assert.Equal(7, all.Count);
        Assert.Equal(7, all.Distinct().Count());
        Assert.True(third.Exhausted);
    }

    [Fact]
    public async Task The_search_stops_when_its_time_is_spent_and_goes_on_from_where_it_got_to()
    {
        using var w = new Workspace(o => o.SearchBudget = TimeSpan.Zero);
        await w.WriteAsync(
            Event(Now.AddDays(-0), "today"),
            Event(Now.AddDays(-1), "yesterday"),
            Event(Now.AddDays(-2), "needle two days ago"));

        var found = new List<string>();
        LogCursor? cursor = null;
        var partials = 0;
        for (var round = 0; round < 10; round++)
        {
            var page = await w.SearchAsync("needle", cursor);
            found.AddRange(page.Events.Select(e => e.Message!));
            partials += page.Partial ? 1 : 0;
            if (page.Exhausted) break;
            cursor = page.Resume;
        }

        Assert.Equal(new[] { "needle two days ago" }, found);
        Assert.True(partials >= 1, "with no time left the search must have stopped at least once");
    }

    [Fact]
    public async Task A_line_that_is_cut_off_is_ignored()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddMinutes(-1), "intact"));
        var file = w.Files("log-*.jsonl").Single();
        await File.AppendAllTextAsync(Path.Combine(w.Directory, file), "{\"i\":1,\"t\":\"2026-10-07T19:2");

        var page = await w.SearchAsync();

        Assert.Equal(new[] { "intact" }, page.Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Nothing_is_found_in_an_empty_folder()
    {
        using var w = new Workspace();

        var page = await w.SearchAsync("anything");

        Assert.Empty(page.Events);
        Assert.True(page.Exhausted);
    }

    [Fact]
    public async Task Whatever_is_typed_stays_a_value_and_never_becomes_sql()
    {
        using var w = new Workspace();
        await w.WriteAsync(Event(Now.AddMinutes(-1), "safe", properties: ("UserName", "bob")));

        foreach (var attack in new[] { "' OR 1=1 --", "bob'); DROP TABLE x; --", "\"; COPY (SELECT 1) TO 'x'; \"" })
        {
            Assert.Empty((await w.SearchAsync($"UserName = '{attack}'")).Events);
            Assert.Empty((await w.SearchAsync(attack.Replace("(", "").Replace(")", ""))).Events);
        }

        Assert.Single((await w.SearchAsync()).Events);
    }

    [Fact]
    public async Task A_query_that_cannot_be_read_is_reported_not_swallowed()
    {
        using var w = new Workspace();

        await Assert.ThrowsAsync<FormatException>(() => w.SearchAsync("hello UserName = bob"));
        await Assert.ThrowsAsync<FormatException>(() => w.Reader.QueryAsync(new LogQueryFilter { LevelFilter = "Nope" }, null, 10));
    }
}
