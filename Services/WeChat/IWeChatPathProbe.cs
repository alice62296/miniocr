namespace MiniOcr.Services;

/// <summary>
/// Filesystem view used by <see cref="WeChatOcrLocator"/>.
/// Production uses the real disk; tests use an in-memory layout.
/// </summary>
public interface IWeChatPathProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    DateTime LastWriteTimeUtc(string path);

    /// <summary>
    /// Files named <paramref name="fileName"/> under <paramref name="root"/>.
    /// Depth 0 is <paramref name="root"/> itself; depth 1 is an immediate child directory.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string root, string fileName, int maxDepth);
}

public sealed class FileSystemWeChatProbe : IWeChatPathProbe
{
    public static readonly FileSystemWeChatProbe Instance = new();

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public DateTime LastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    public IEnumerable<string> EnumerateFiles(string root, string fileName, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || maxDepth < 0 || !DirectoryExists(root))
            yield break;

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = maxDepth > 0,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, fileName, options);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
            yield return file;
    }
}
