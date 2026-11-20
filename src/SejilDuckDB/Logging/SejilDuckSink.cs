using Serilog.Core;
using Serilog.Debugging;
using SejilDuckDB.Storage;

namespace SejilDuckDB.Logging;

/// <summary>
/// The local sink: it puts the events (those at or above the minimum level of the folder) in the queue of the folder, the same one through which the route <c>/log</c> writes, and
/// returns at once. <c>Emit</c> never blocks and never throws. The queue writes by batches, keeps what it could not write and
/// tries again, and drops the newest events when it is full (reported in the Serilog SelfLog).
/// </summary>
internal sealed class SejilDuckSink : ILogEventSink, IDisposable
{
    private readonly LogFileStore _store;
    private readonly string _sourceApp;
    private readonly TimeSpan _flushTimeout;

    public SejilDuckSink(LogFileStore store, string sourceApp, TimeSpan flushTimeout)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sourceApp = sourceApp ?? throw new ArgumentNullException(nameof(sourceApp));
        _flushTimeout = flushTimeout;
    }

    public void Emit(Serilog.Events.LogEvent logEvent)
    {
        try
        {
            _store.TryEnqueue(new[] { SerilogEventConverter.ToEvent(logEvent) }, _sourceApp);
        }
        catch (Exception e)
        {
            SelfLog.WriteLine("SejilDuckDB: an event could not be queued: {0}", e.Message);
        }
    }

    /// <summary>Writes what is still waiting, whoever put it there; gives up after the flush timeout when the folder cannot be written.</summary>
    public void Dispose()
    {
        if (!_store.Queue.FlushAsync(_flushTimeout).GetAwaiter().GetResult())
        {
            SelfLog.WriteLine("SejilDuckDB: {0} log events could not be written and were lost at shutdown.", _store.Queue.Pending);
        }

        var dropped = _store.Queue.TakeDropped();
        if (dropped > 0)
        {
            SelfLog.WriteLine("SejilDuckDB: {0} log events were dropped because the queue was full.", dropped);
        }
    }
}
