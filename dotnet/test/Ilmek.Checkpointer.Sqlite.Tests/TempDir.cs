using Microsoft.Data.Sqlite;

namespace Ilmek.Checkpointers.Sqlite.Tests;

/// <summary>
/// Teardown for the per-test database directory.
///
/// Microsoft.Data.Sqlite pools connections, and a pooled connection keeps its
/// file open — on Windows the directory then cannot be deleted. So clear every
/// pool first, then delete with a few short retries (antivirus and the indexer
/// also like to hold fresh files for a moment). Cleanup failing is not a test
/// failure: the directory lives under the temp folder and the OS reaps it.
/// </summary>
internal static class TempDir
{
    public static void Delete(string dir)
    {
        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            Thread.Sleep(50 * (attempt + 1));
        }
    }
}
