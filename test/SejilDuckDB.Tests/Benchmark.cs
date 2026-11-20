using System.Diagnostics;
using SejilDuckDB.Query;
using SejilDuckDB.Storage;
using Xunit.Abstractions;
using static SejilDuckDB.Tests.Workspace;

namespace SejilDuckDB.Tests;

/// <summary>
/// Measures the searches at the volume the site is meant for (100 000 events a day). Slow and heavy on the disk, so it only runs on
/// request: <c>SEJIL_BENCH=1 dotnet test --filter Benchmark --logger "console;verbosity=detailed"</c>. Days are set with <c>SEJIL_BENCH_DAYS</c> (7).
/// </summary>
public class Benchmark(ITestOutputHelper output)
{
    private const int PerDay = 100_000;

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Searches_at_a_hundred_thousand_events_a_day()
    {
        if (Environment.GetEnvironmentVariable("SEJIL_BENCH") != "1")
        {
            return; // not asked for
        }

        var days = int.TryParse(Environment.GetEnvironmentVariable("SEJIL_BENCH_DAYS"), out var d) ? d : 7;
        using var w = new Workspace(o => o.SearchBudget = TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.Now;
        var random = new Random(42);
        string[] apps = { "OIM", "ECS", "FRW", "Billing", "Portal" };
        string[] templates =
        {
            "GET /api/orders/{0} responded 200 in {1} ms",
            "Connexion à la base établie pour l'utilisateur {0}",
            "Formulaire {0} validé en {1} ms",
            "Cache miss for key user:{0}",
        };

        var clock = Stopwatch.StartNew();
        for (var day = 0; day < days; day++)
        {
            var batch = new List<LogEvent>(10_000);
            for (var n = 0; n < PerDay; n++)
            {
                var at = now.Date.AddDays(-day).AddSeconds(random.Next(0, 86_000)).AddTicks(random.Next(0, 10_000));
                var id = random.Next(1_000_000);
                var level = random.Next(100) < 2 ? 4 : random.Next(100) < 10 ? 3 : 2;
                var text = string.Format(templates[random.Next(templates.Length)], id, random.Next(5, 900));
                batch.Add(Event(at, text, level, level == 4 ? "System.InvalidOperationException: boom\n   at A.B()\n   at C.D()" : null,
                    ("UserName", "user" + random.Next(500)), ("RequestPath", "/forms/" + random.Next(50)), ("StatusCode", level == 4 ? "500" : "200"), ("Server", "srv-" + random.Next(2))));
                if (batch.Count == 10_000)
                {
                    Assert.True(await w.Store.TryAppendAsync(batch, apps[random.Next(apps.Length)]));
                    batch.Clear();
                }
            }
        }

        var bytes = new DirectoryInfo(w.Directory).GetFiles("log-*").Sum(f => f.Length);
        output.WriteLine($"written: {days * PerDay:N0} events in {clock.Elapsed.TotalSeconds:F1} s -> {bytes / 1_048_576.0:F0} MB ({bytes / (double)(days * PerDay):F0} bytes/event)");

        async Task Time(string name, string? query, Func<LogPage, bool>? check = null)
        {
            // first run includes loading DuckDB and the files into the OS cache; the second is the steady state
            var first = Stopwatch.StartNew();
            var page = await w.SearchAsync(query);
            var cold = first.Elapsed;
            var second = Stopwatch.StartNew();
            page = await w.SearchAsync(query);
            output.WriteLine($"{name,-52} {cold.TotalMilliseconds,7:F0} ms (first)  {second.Elapsed.TotalMilliseconds,7:F0} ms (next)  -> {page.Events.Count} events, exhausted={page.Exhausted}, partial={page.Partial}, scanned back to {page.ScannedTo}");
            if (check is not null) Assert.True(check(page), name);
        }

        output.WriteLine("--- plain files");
        await Time("newest page, no filter", null);
        await Time("word that is common ('Formulaire')", "Formulaire");
        await Time("level = Error", "level = Error");
        await Time("property: UserName = user42", "UserName = user42");
        await Time("rare word, found once ('user:123456')", "user:123456");
        await Time("word that matches nothing (reads everything)", "zzz-nothing-zzz", p => p.Events.Count == 0 && p.Exhausted);

        new LogMaintenance(w.Store, compressAfterDays: 1, retentionDays: null).Run(DateTime.Now);
        var gz = new DirectoryInfo(w.Directory).GetFiles("*.gz").Sum(f => f.Length);
        output.WriteLine($"--- compressed: {w.Files("*.gz").Length} files, {gz / 1_048_576.0:F0} MB (was {bytes / 1_048_576.0:F0} MB)");
        await Time("newest page, no filter", null);
        await Time("property: UserName = user42", "UserName = user42");
        await Time("word that matches nothing (reads everything)", "zzz-nothing-zzz", p => p.Events.Count == 0 && p.Exhausted);
    }
}
