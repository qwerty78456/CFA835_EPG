namespace Cfa835SystemMonitor;

/// <summary>
/// What the reloader compares to decide that a configuration file changed. Existence, last write
/// time and length together catch every save an editor performs (truncate-and-write, temp-file
/// rename, copy-over) without reading the file.
/// </summary>
public readonly record struct FileStamp(bool Exists, DateTime LastWriteTimeUtc, long Length)
{
    /// <summary>Stats <paramref name="path"/>. Never throws: an unreadable path stamps as missing.</summary>
    public static FileStamp Of(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return default;
        }
    }
}

/// <summary>One file a configuration snapshot was built from, stamped before it was read.</summary>
public sealed record TrackedFile(string Path, FileStamp Stamp);

public static class SharedFile
{
    /// <summary>
    /// Reads a text file while letting other processes keep writing, renaming or deleting it.
    /// <see cref="File.ReadAllText(string)"/> denies writers for the duration of the read, which
    /// would make an operator's editor fail to save whenever the monitor happens to be reading.
    /// </summary>
    public static string ReadAllText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
