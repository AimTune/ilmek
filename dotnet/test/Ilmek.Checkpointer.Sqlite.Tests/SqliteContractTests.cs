using Ilmek;
using Ilmek.Tests.Contract;

namespace Ilmek.Checkpointers.Sqlite.Tests;

/// <summary>The same §7 contract the in-memory backend passes, against a real SQLite file.</summary>
public sealed class SqliteContractTests : CheckpointerContract, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ilmek-sqlite-contract-").FullName;
    private readonly List<SqliteCheckpointer> _opened = new();

    protected override ICheckpointer Create()
    {
        var cp = SqliteCheckpointer.Open(Path.Combine(_dir, $"c{_opened.Count}.db"));
        _opened.Add(cp);
        return cp;
    }

    public void Dispose()
    {
        foreach (var cp in _opened) cp.Dispose();
        TempDir.Delete(_dir);
    }
}
