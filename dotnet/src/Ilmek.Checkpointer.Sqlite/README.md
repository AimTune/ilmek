# Ilmek.Checkpointer.Sqlite

A durable [ilmek](https://github.com/AimTune/ilmek) checkpointer in a single
SQLite file (Microsoft.Data.Sqlite). A parked interrupt survives a process
restart, a deploy, or a crash.

```csharp
using Ilmek;
using Ilmek.Checkpointers.Sqlite;

using var cp = SqliteCheckpointer.Open("./agent.db");   // creates + migrates
var paused = await graph.RunAsync(input, new RunOptions { ThreadId = "t1", Checkpointer = cp });
// …process restarts…
using var cp2 = SqliteCheckpointer.Open("./agent.db");
var done = await graph.ResumeAsync("yes", new RunOptions { ThreadId = "t1", Checkpointer = cp2 });
```

`SqliteCheckpointerOptions` sets `TablePrefix` (default `"ilmek"`, must be a plain
SQL identifier) and `Wal` (default `true`); `new SqliteCheckpointer(connection)`
uses a connection you own. Values come back from the file in their JSON shape —
see <https://ilmek.aimtune.dev/checkpointers/sqlite>.
