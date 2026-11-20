using System.Text.RegularExpressions;
using SejilDuckDB.Storage;
using static SejilDuckDB.Tests.Workspace;

namespace SejilDuckDB.Tests;

/// <summary>
/// Writers that run at the same time must never overwrite each other. (FileMode.Append is not an atomic append in .NET: before
/// this was tested, concurrent writes lost about one event in ten.)
/// </summary>
public class ConcurrencyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static string IdOf(string line) => Regex.Match(line, @"""i"":(\d+)").Groups[1].Value;

    private static List<string> LinesIn(Workspace w)
        => System.IO.Directory.GetFiles(w.Directory, "log-*.jsonl").SelectMany(File.ReadAllLines).ToList();

    private static LogEvent[] Batch(string prefix, int size)
        => Enumerable.Range(0, size).Select(i => Event(Now.AddMilliseconds(-i), $"{prefix}-{i}")).ToArray();

    [Fact]
    public async Task Many_requests_at_once_lose_nothing()
    {
        using var w = new Workspace();

        // 40 senders at once, as many applications posting to /log
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(sender => Task.Run(async () =>
        {
            var ok = true;
            for (var batch = 0; batch < 15; batch++)
            {
                ok &= await w.Store.TryAppendAsync(Batch($"s{sender}-b{batch}", 20), $"App{sender}");
            }

            return ok;
        })));

        Assert.All(results, Assert.True);
        var lines = LinesIn(w);
        Assert.Equal(40 * 15 * 20, lines.Count);                                  // 12 000, not 10 800 or so
        Assert.Equal(lines.Count, lines.Select(IdOf).Distinct().Count());         // and every id is its own
        Assert.All(lines, l => Assert.StartsWith("{", l));                         // no line cut in two by another
        Assert.All(lines, l => Assert.EndsWith("}", l));
    }

    [Fact]
    public async Task Two_stores_on_one_folder_lose_nothing()
    {
        using var w = new Workspace();
        var other = new LogFileStore(w.Directory); // as when a process that does not host the site writes to the same folder

        async Task Write(LogFileStore store, string prefix)
        {
            for (var batch = 0; batch < 20; batch++)
            {
                Assert.True(await store.TryAppendAsync(Batch($"{prefix}{batch}", 50), prefix));
            }
        }

        await Task.WhenAll(Write(w.Store, "a"), Write(other, "b"), Write(w.Store, "c"), Write(other, "d"));

        var lines = LinesIn(w);
        Assert.Equal(4 * 20 * 50, lines.Count);
        Assert.Equal(lines.Count, lines.Select(IdOf).Distinct().Count());
    }

    [Fact]
    public async Task Writing_while_searches_run_loses_nothing_and_no_search_fails()
    {
        using var w = new Workspace();
        await w.WriteAsync(Batch("seed", 100)); // there is a file to read from the start
        using var stop = new CancellationTokenSource();

        var searches = Task.Run(async () =>
        {
            var done = 0;
            while (!stop.IsCancellationRequested)
            {
                var page = await w.SearchAsync(pageSize: 50); // reads the file that is being written to
                Assert.NotEmpty(page.Events);
                done++;
            }

            return done;
        });

        var writers = await Task.WhenAll(Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            var ok = true;
            for (var batch = 0; batch < 20; batch++)
            {
                ok &= await w.Store.TryAppendAsync(Batch($"w{n}-{batch}", 25), $"W{n}");
            }

            return ok;
        })));

        stop.Cancel();
        var searchesDone = await searches;

        Assert.All(writers, Assert.True);
        Assert.Equal(100 + 6 * 20 * 25, LinesIn(w).Count);
        Assert.True(searchesDone > 0, "no search ran while the writers did");
    }
}
