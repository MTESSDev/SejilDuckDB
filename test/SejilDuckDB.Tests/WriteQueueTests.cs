using SejilDuckDB.Storage;
using static SejilDuckDB.Tests.Workspace;

namespace SejilDuckDB.Tests;

public class WriteQueueTests
{
    private delegate Task<bool> Write(IReadOnlyList<(LogEvent Event, string App)> batch);

    private static LogEvent Ev(string message) => Event(DateTimeOffset.Now, message);

    private static WriteQueue Queue(Write write, int limit = 1000, int windowMs = 0, int retryMs = 20)
        => new(batch => write(batch), limit, TimeSpan.FromMilliseconds(windowMs), TimeSpan.FromMilliseconds(retryMs));

    // ---------- the route: told the truth, keeps nothing ----------

    [Fact]
    public async Task A_submit_returns_when_the_events_are_written_not_before()
    {
        var release = new TaskCompletionSource();
        var queue = Queue(async _ => { await release.Task; return true; });

        var submitted = queue.SubmitAsync(new[] { Ev("a") }, "App");
        await Task.Delay(100);

        Assert.False(submitted.IsCompleted, "the route must not answer 200 for events that are not on disk");
        release.SetResult();
        Assert.True(await submitted);
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task A_submit_whose_write_failed_says_so_and_keeps_nothing_so_the_sender_can_try_again()
    {
        var calls = 0;
        var queue = Queue(_ => { Interlocked.Increment(ref calls); return Task.FromResult(false); });

        Assert.False(await queue.SubmitAsync(new[] { Ev("a") }, "App"));
        await Task.Delay(150);

        Assert.Equal(1, calls);                 // not retried here: the sender does it, and a retry here would duplicate
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task A_writer_that_throws_is_a_failed_write_not_a_crashed_queue()
    {
        var calls = 0;
        var queue = Queue(_ => ++calls == 1 ? throw new IOException("disk full") : Task.FromResult(true));

        Assert.False(await queue.SubmitAsync(new[] { Ev("a") }, "App"));
        Assert.True(await queue.SubmitAsync(new[] { Ev("b") }, "App"));   // the queue is still alive
    }

    [Fact]
    public async Task Many_submits_at_once_are_written_by_a_few_appends_and_each_is_told_it_succeeded()
    {
        var writes = new List<int>();
        var queue = Queue(batch => { lock (writes) writes.Add(batch.Count); return Task.FromResult(true); }, windowMs: 50);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => queue.SubmitAsync(new[] { Ev($"e{i}") }, "App")));

        Assert.All(results, Assert.True);
        Assert.Equal(100, writes.Sum());
        Assert.True(writes.Count <= 5, $"100 requests should cost a few appends, not {writes.Count}");
    }

    [Fact]
    public async Task Each_batch_keeps_the_name_of_the_application_that_sent_it()
    {
        var seen = new List<(string Message, string App)>();
        var queue = Queue(batch => { lock (seen) seen.AddRange(batch.Select(b => (b.Event.Message!, b.App))); return Task.FromResult(true); }, windowMs: 30);

        await Task.WhenAll(
            queue.SubmitAsync(new[] { Ev("from billing") }, "Billing"),
            queue.SubmitAsync(new[] { Ev("from portal") }, "Portal"));

        Assert.Contains(("from billing", "Billing"), seen);
        Assert.Contains(("from portal", "Portal"), seen);
    }

    [Fact]
    public async Task Events_are_written_in_the_order_they_were_put_in()
    {
        var order = new List<string>();
        var queue = Queue(batch => { lock (order) order.AddRange(batch.Select(b => b.Event.Message!)); return Task.FromResult(true); }, windowMs: 20);

        for (var i = 0; i < 50; i++) queue.TryEnqueue(new[] { Ev($"e{i:D2}") }, "App");
        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"e{i:D2}"), order);
    }

    // ---------- the sink: instant, patient, bounded ----------

    [Fact]
    public async Task An_enqueue_returns_at_once_even_when_the_folder_is_slow()
    {
        var release = new TaskCompletionSource();
        var queue = Queue(async _ => { await release.Task; return true; });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 500; i++) Assert.True(queue.TryEnqueue(new[] { Ev($"e{i}") }, "App"));

        Assert.True(clock.ElapsedMilliseconds < 1000, "TryEnqueue blocked the caller");
        release.SetResult();
        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Enqueued_events_that_could_not_be_written_are_kept_and_written_later_without_duplicates()
    {
        var attempts = 0;
        var written = new List<string>();
        var queue = Queue(batch =>
        {
            if (Interlocked.Increment(ref attempts) <= 2) return Task.FromResult(false);   // the folder is unavailable twice
            lock (written) written.AddRange(batch.Select(b => b.Event.Message!));
            return Task.FromResult(true);
        });

        queue.TryEnqueue(new[] { Ev("a") }, "App");
        queue.TryEnqueue(new[] { Ev("b") }, "App");
        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(new[] { "a", "b" }, written);
        Assert.True(attempts >= 3);
    }

    [Fact]
    public async Task A_full_queue_refuses_a_submit_and_drops_the_newest_enqueues_counting_them()
    {
        var release = new TaskCompletionSource();
        var written = new List<string>();
        var queue = Queue(async batch => { await release.Task; lock (written) written.AddRange(batch.Select(b => b.Event.Message!)); return true; }, limit: 5);

        Assert.True(queue.TryEnqueue(new[] { Ev("first") }, "App"));
        await Task.Delay(100); // the writer has taken it and waits
        for (var i = 0; i < 4; i++) Assert.True(queue.TryEnqueue(new[] { Ev($"kept{i}") }, "App"));   // 1 + 4 = the limit
        Assert.False(queue.TryEnqueue(new[] { Ev("dropped1") }, "App"));
        Assert.False(queue.TryEnqueue(new[] { Ev("dropped2") }, "App"));
        Assert.False(await queue.SubmitAsync(new[] { Ev("refused") }, "App"));   // the route answers 503: the sender backs off

        Assert.Equal(2, queue.TakeDropped());
        Assert.Equal(0, queue.TakeDropped());

        release.SetResult();
        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "first", "kept0", "kept1", "kept2", "kept3" }, written);   // the oldest kept, the newest dropped
    }

    [Fact]
    public async Task A_single_batch_bigger_than_the_limit_is_accepted_when_the_queue_is_empty()
    {
        var queue = Queue(_ => Task.FromResult(true), limit: 3);

        Assert.True(await queue.SubmitAsync(Enumerable.Range(0, 10).Select(i => Ev($"e{i}")).ToArray(), "App")); // else it could never be written
    }

    // ---------- both together ----------

    [Fact]
    public async Task A_submit_and_an_enqueue_in_the_same_batch_are_both_written_and_a_failure_is_told_only_to_the_submit()
    {
        var failFirst = true;
        var written = new List<string>();
        var queue = Queue(batch =>
        {
            if (failFirst) { failFirst = false; return Task.FromResult(false); }
            lock (written) written.AddRange(batch.Select(b => b.Event.Message!));
            return Task.FromResult(true);
        }, windowMs: 100);

        queue.TryEnqueue(new[] { Ev("sink event") }, "Local");
        var told = await queue.SubmitAsync(new[] { Ev("route event") }, "Remote");

        Assert.False(told);                                              // the sender of the route is told, and sends again
        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(5)));    // the sink's event is kept and written
        Assert.Equal(new[] { "sink event" }, written);
    }

    [Fact]
    public async Task A_flush_waits_for_everything_and_tries_again_at_once_instead_of_after_the_usual_delay()
    {
        var attempts = 0;
        var queue = new WriteQueue(_ => Task.FromResult(Interlocked.Increment(ref attempts) > 2), 1000, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        queue.TryEnqueue(new[] { Ev("a") }, "App");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(10)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the flush waited for the 30 s retry delay: {clock.Elapsed}");
    }

    [Fact]
    public async Task A_flush_gives_up_after_its_time_when_the_folder_stays_unwritable()
    {
        var queue = Queue(_ => Task.FromResult(false));
        queue.TryEnqueue(new[] { Ev("never written") }, "App");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var flushed = await queue.FlushAsync(TimeSpan.FromMilliseconds(300));

        Assert.False(flushed);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 3000);
        Assert.Equal(1, queue.Pending);
    }

    [Fact]
    public async Task Nothing_to_write_is_not_a_write()
    {
        var calls = 0;
        var queue = Queue(_ => { calls++; return Task.FromResult(true); });

        Assert.True(await queue.SubmitAsync(Array.Empty<LogEvent>(), "App"));
        Assert.True(queue.TryEnqueue(Array.Empty<LogEvent>(), "App"));
        await Task.Delay(100);

        Assert.Equal(0, calls);
    }

    // ---------- one queue per folder ----------

    [Fact]
    public void Every_store_on_a_folder_shares_its_queue_and_two_folders_do_not()
    {
        using var a = new Workspace();
        using var b = new Workspace();

        var second = new LogFileStore(a.Directory);

        Assert.Same(a.Store.Queue, second.Queue);           // the route and a sink on the same folder: one queue
        Assert.NotSame(a.Store.Queue, b.Store.Queue);       // another folder: another queue, so that one cannot block the other
    }

    [Fact]
    public async Task The_events_of_the_route_and_of_the_sink_end_up_in_the_same_files_through_the_same_queue()
    {
        using var w = new Workspace();
        var other = new LogFileStore(w.Directory); // a second store: what a sink built from the folder uses

        other.Queue.TryEnqueue(new[] { Ev("from the sink") }, "Local");
        Assert.True(await w.Store.TryAppendAsync(new[] { Ev("from the route") }, "Remote"));
        Assert.True(await w.Store.Queue.FlushAsync(TimeSpan.FromSeconds(5)));

        var page = await w.SearchAsync();
        Assert.Equal(new[] { "Local", "Remote" }.Order(), page.Events.Select(e => e.SourceApp).Order());
    }
}
