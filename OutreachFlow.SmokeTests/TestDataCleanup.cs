using Microsoft.Data.Sqlite;

/// <summary>
/// Cleans only test-created paths. SQLite WAL files and Windows file handles may
/// outlive DbContext.Dispose briefly, so close pools and retry boundedly.
/// Cleanup failure is a failed test, never an ignored exception.
/// </summary>
internal static class TestDataCleanup
{
    public static async Task DirectoryAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await RetryAsync(() =>
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }, path);
    }

    public static async Task SqliteFileAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await RetryAsync(() =>
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }, path);
    }

    private static async Task RetryAsync(Action cleanup, string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { cleanup(); return; }
            catch (IOException e) { last = e; }
            catch (UnauthorizedAccessException e) { last = e; }
            await Task.Delay(150 * (attempt + 1));
        }
        throw new IOException("Temporary test data could not be removed: " + path, last);
    }
}
