using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SejilDuckDB.Logging;
using SejilDuckDB.Storage;
using Serilog.Configuration;
using Serilog.Events;

namespace Serilog;

public static class SejilDuckLoggerConfigurationExtensions
{
    /// <summary>
    /// Writes the log events of this application straight to the log files of the site, with no HTTP in between. It can be used
    /// <b>together with</b> the HTTP route: both put their events in the same queue and the same files, and the viewer shows both.
    /// <code>
    /// builder.Services.AddSejilDuck(o => o.DataDirectory = @"D:\logs");
    /// builder.Host.UseSerilog((context, services, logger) => logger.WriteTo.SejilDuck(services));
    /// </code>
    /// The events go into the one queue through which the folder is written (the one the route <c>/log</c> uses): the application is
    /// never slowed down, they are written by batches, what cannot be written is kept and tried again, and when the queue is full the
    /// newest are dropped (reported in the Serilog SelfLog; the queue holds <c>SejilDuckOptions.QueueLimit</c> events). At shutdown the
    /// queue is flushed.
    /// </summary>
    /// <param name="sinkConfiguration">Where the sink is added.</param>
    /// <param name="services">The services of the application, where <c>AddSejilDuck()</c> was called: the sink then shares the site's files and its queue.</param>
    /// <param name="sourceApp">The application name shown in the viewer. The name of the entry assembly by default.</param>
    /// <param name="restrictedToMinimumLevel">The minimum level of the events that reach this sink.</param>
    /// <param name="flushTimeoutSeconds">At shutdown, how long the queued events are retried when the files cannot be written. 10 by default.</param>
    public static LoggerConfiguration SejilDuck(
        this LoggerSinkConfiguration sinkConfiguration,
        IServiceProvider services,
        string? sourceApp = null,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        int flushTimeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(services);

        var store = services.GetService<LogFileStore>()
            ?? throw new InvalidOperationException("SejilDuckDB is not registered: call services.AddSejilDuck() before WriteTo.SejilDuck(services).");

        return Add(sinkConfiguration, store, sourceApp, restrictedToMinimumLevel, flushTimeoutSeconds);
    }

    /// <summary>
    /// The same, for a logger that is built before the application (a bootstrap logger) or in a process that does not host the site:
    /// it appends to the files of the folder, through the queue of that folder in this process. The compression and the purge of the old
    /// files are done by the site that owns the folder.
    /// </summary>
    /// <param name="sinkConfiguration">Where the sink is added.</param>
    /// <param name="dataDirectory">The folder of the log files (<c>SejilDuckOptions.DataDirectory</c> of the site).</param>
    /// <param name="sourceApp">The application name shown in the viewer. The name of the entry assembly by default.</param>
    /// <param name="restrictedToMinimumLevel">The minimum level of the events that reach this sink.</param>
    /// <param name="queueLimit">Events waiting to be written; beyond it the newest are dropped. 50000 by default. Only counts when this sink is the first to write to the folder in this process.</param>
    /// <param name="flushTimeoutSeconds">At shutdown, how long the queued events are retried when the files cannot be written. 10 by default.</param>
    public static LoggerConfiguration SejilDuck(
        this LoggerSinkConfiguration sinkConfiguration,
        string dataDirectory,
        string? sourceApp = null,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        int queueLimit = WriteQueue.DefaultLimit,
        int flushTimeoutSeconds = 10)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A folder is required.", nameof(dataDirectory));
        }

        return Add(sinkConfiguration, new LogFileStore(Path.GetFullPath(dataDirectory), queueLimit), sourceApp, restrictedToMinimumLevel, flushTimeoutSeconds);
    }

    private static LoggerConfiguration Add(
        LoggerSinkConfiguration sinkConfiguration, LogFileStore store, string? sourceApp, LogEventLevel restrictedToMinimumLevel, int flushTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);

        var app = string.IsNullOrWhiteSpace(sourceApp)
            ? Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName
            : sourceApp;

        return sinkConfiguration.Sink(new SejilDuckSink(store.Queue, app, TimeSpan.FromSeconds(flushTimeoutSeconds)), restrictedToMinimumLevel);
    }
}
