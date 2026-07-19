# Ilmek.Checkpointer.Sqlite

A durable [ilmek](https://github.com/AimTune/ilmek) checkpointer in a single
SQLite file (Microsoft.Data.Sqlite). A parked interrupt survives a process
restart, a deploy, or a crash.

```csharp
using var cp = SqliteCheckpointer.Open("./agent.db");   // creates + migrates
var paused = await graph.RunAsync(input, new RunOptions { ThreadId = "t1", Checkpointer = cp });
// …process restarts…
using var cp2 = SqliteCheckpointer.Open("./agent.db");
var done = await graph.ResumeAsync("yes", new RunOptions { ThreadId = "t1", Checkpointer = cp2 });
```
