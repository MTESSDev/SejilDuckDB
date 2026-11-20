# SejilDuckDB

> **Beta** (`0.1.0-beta.2`). Tested with real files, a real DuckDB and two real servers behind a header-aware balancer, but not yet in production behind IIS and the real load balancer.

A log site for ASP.NET Core with **no database server**. Applications send their logs with `Serilog.Sinks.Http`; the site keeps them in **local JSON Lines files** (one per day, compressed to `.gz` after a few days, deleted after a retention) and a web viewer searches them with **DuckDB**.

It is built from the viewer of [SejilSQL](https://github.com/MTESSDev/SejilSQL) (itself a fork of [Sejil](https://github.com/alaatm/Sejil) by Alaa Masoud), with the same query language.

With **several servers**, each one writes only to its own disk. The viewer asks every server (the load balancer is told which one with a header, `x-nlb2`) and **merges the answers**.

```
Applications â”€â”€(Serilog.Sinks.Http  POST /log)â”€â”€â–º  load balancer â”€â”€â–º server A  (its own files)
                                                                 â””â”€â”€â–º server B  (its own files)

Browser â”€â”€â–º viewer served by A or B
        â”œâ”€â”€ POST /logs/events   x-nlb2: srv-a â”€â”€â–º A
        â””â”€â”€ POST /logs/events   x-nlb2: srv-b â”€â”€â–º B        ... and merges the two answers
```

## Getting started

```csharp
using SejilDuckDB;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSejilDuck(options =>
{
    options.DataDirectory = @"D:\logs";          // where this server keeps its files
    options.Title = "Logs";
});

var app = builder.Build();
app.MapSejilDuck();                              // the viewer on /logs, its API, and POST /log
app.Run();
```

Open **https://your-site/logs**. See [sample/SampleApp](sample/SampleApp) (the site) and [sample/SampleSender](sample/SampleSender) (an application that sends its logs).

In the sending application:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Http(requestUri: "https://your-site/log?sourceApp=MyApp", queueLimitBytes: 1_048_576)
    .CreateLogger();
```

The ingest route is anonymous (the senders have no identity to present). It answers `200` only once the events are **written to the files**; when it cannot write them (or the queue is full) it answers `503`, so the sender keeps its batch and tries again. The viewer and its API can be put behind an authentication scheme (`AuthenticationScheme`).

## The site's own logs: a local sink, with no HTTP

The application that hosts the site can write its own logs straight to the files, without posting them to itself. It can do so **at the same time** as it receives the logs of other applications over HTTP:

```csharp
builder.Services.AddSejilDuck(o => o.DataDirectory = @"D:\logs");
builder.Host.UseSerilog((context, services, logger) => logger
    .WriteTo.SejilDuck(services, sourceApp: "MySite"));   // needs AddSejilDuck() above
```

For a logger built before the application (a bootstrap logger), or a process that does not host the site, give it the folder: `.WriteTo.SejilDuck(@"D:\logs", "MyApp")`. The compression and the purge of the old files stay with the site that owns the folder.

**One queue.** Everything that is written to a folder in a process goes through one queue: the batches received on `/log` and the events of the sink alike. A single writer takes what has gathered (about 10 ms' worth, at most 5000 events) and appends it to each day's file in one go, so a hundred requests at once cost a few appends. The route waits for its events to be written before it answers; the sink does not wait (it never blocks the application, keeps what it could not write and tries again, and drops the newest events when the queue is full, at `QueueLimit`). At shutdown the queue is flushed. The same event leaves the same line whichever way it came (property values as text, whatever the culture of the machine).

## Several servers

Give **every server the same list** of header values, and its own name:

```csharp
options.NodeName = "srv-a";                      // this server (the machine name by default)
options.NodeHeader = "x-nlb2";                   // the header the load balancer reads
options.Nodes = new() { "srv-a", "srv-b" };      // the header values that reach each server, this one included
```

The load balancer must send a request to the server named by the header, and anywhere when there is none (that is what the applications' `POST /log` do). The list travels **inside the page** the server sends, so it is there even when one server is down.

What the viewer does:

- **Searches** go to every server and are merged, newest first, a page at a time. Each row shows its server. A server that does not answer is reported (*"results are partial"*, with *Retry*): the others still show.
- **Saved queries and the minimum log level** are written to **every** server, and refused if one of them fails, so that they cannot disagree without anyone knowing. Repeating the change is harmless.
- If every call reaches the same server (the header is not honoured), the viewer says so, and each event still appears once.

## Files

| | |
|---|---|
| `log-yyyyMMdd.jsonl` | the events of a day (by the local date of the event), one JSON object per line, appended to |
| `log-yyyyMMdd.<stamp>.jsonl.gz` | the same, compressed, once the day is older than `CompressAfterDays` (7) |
| `queries.json`, `config.json` | the saved queries and the shared minimum level of this server |

A line: `{"i":id,"t":"utc","d":"local","a":"app","l":level,"m":"message","x":"exception","p":{"Name":"value"}}`.
Everything is plain text: you can read the files, copy them, or search them with `duckdb` yourself.

A maintenance service runs every hour: it compresses the days past `CompressAfterDays` (null: never) and deletes those past `RetentionDays` (90; null: never). An event that arrives late for a day that is already compressed goes into a new `.jsonl` beside it.

## Searching

Newest first, **one day at a time**: a page is full as soon as enough events are found, so the newest events cost the same whatever the size of the history. A search that finds little reads back through the days; after `SearchBudget` (8 s) it hands back what it found and the viewer asks again from where it stopped (it shows *"Searchingâ€¦ back to 2026-11-02"*).

The query language is the one of SejilSQL: a word (message, exception and properties), `UserName = bob`, `RequestPath like '%forms%'`, `level = Error`, `sourceApp = 'OIM' and message like '%timeout%'`, with `and` / `&&`, `or` / `||` and parentheses. Differences:

- a property **name is case sensitive** (`UserName`, not `username`); its **value** is not;
- a bare word is also looked for in the property **names** (properties are searched as text).

Measured on this machine (10-core ARM laptop CPU, SSD) with **100 000 events a day**, 7 days (700 000 events, 245 bytes each, 163 MB; 15 MB compressed). The numbers are for 7 days, not 90 â€” the searches that read everything scale with the number of days:

| Search | Time |
|---|---|
| newest page, no filter | ~50-80 ms |
| common word, a level, a property (page fills at once) | ~50-130 ms |
| rare word / matches nothing (reads all 7 days) | ~0.8-0.9 s |
| the same on the compressed files | ~0.9 s |

Run it yourself: `SEJIL_BENCH=1 SEJIL_BENCH_DAYS=7 dotnet test --filter Category=Benchmark --logger "console;verbosity=detailed"`.

## Settings

All on `SejilDuckOptions` (bindable from configuration, see the sample's `appsettings.json`):

| | default |
|---|---|
| `DataDirectory` | `logs-data` (relative to the content root) |
| `Url` / `IngestPath` / `LevelPath` | `/logs` / `/log` / none |
| `Title` / `AuthenticationScheme` | `Sejil` / none |
| `NodeName` / `NodeHeader` / `Nodes` | machine name / `x-nlb2` / empty (one server) |
| `CompressAfterDays` / `RetentionDays` | 7 / 90 (null: never) |
| `PageSize` / `SearchBudget` | 100 / 8 s |
| `MinimumLogLevel` | `Warning` (the level the applications read on `GET /logs/min-log-level`) |
| `QueueLimit` | 50000 events waiting to be written, from `/log` and the sink together |
| `DuckDbMemoryLimit` / `DuckDbThreads` | DuckDB's own |

## Things to know

- **Logs only, and a log can be lost.** There is no replication: when a disk is lost, its logs are. A batch that spans several days is written day by day; if the second write fails, the retry repeats the first (a duplicate, never a loss).
- **Clocks**: servers are merged on the time of the events, so keep them on NTP.
- **Several processes on one folder** (IIS keeps the old process alive while it recycles, or a sink runs in another process) take turns to write: a file is opened for writing by one process at a time, and a second one waits for it (a few milliseconds). Tested with two real processes and eight clients at full speed: nothing lost. (`FileMode.Append` does not append atomically in .NET: the first version of this library let two writers overwrite each other and lost about 6 % of the events with two processes.)
- **A request that is aborted** while its events wait to be written does not take them back: they are written all the same, and a sender that sends them again leaves a duplicate.
- **DuckDB is a native library** (`DuckDB.NET.Data.Full`). Publish for the platform of the server (`win-x64`) and check that it loads there.
- Not covered: the OpenTelemetry traces and metrics (see `AddSejilSlowTraces` in SejilSQL for slow requests and thread pool starvation, which are plain log events and work here too).

## Building

.NET 10 SDK, and Node.js 22 or later to rebuild the viewer:

```powershell
dotnet build
dotnet test                                      # 75 tests, real DuckDB and real files
cd src/SejilDuckDB.Client
npm install
npm test                                         # 33 tests
npm run embed                                    # builds the viewer into src/SejilDuckDB/index.html
cd ../..; dotnet pack src/SejilDuckDB -c Release -o nupkg
```

## License

Apache 2.0. Copyright Â© Alaa Masoud, Dany CÃ´tÃ©. A fork of [Sejil](https://github.com/alaatm/Sejil).
