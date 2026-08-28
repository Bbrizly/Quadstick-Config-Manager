using System.Text;

namespace QuadStick.Infrastructure.Files;

/// <summary>Crash-safe best-effort atomic replacement for ordinary local files.</summary>
public static class AtomicFileWriter
{
    public static void Write(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // A unique sibling per writer prevents concurrent settings/background
        // writes from deleting or publishing each other's staging file.
        var fileName = Path.GetFileName(path);
        var tmp = Path.Combine(directory ?? "", $".{fileName}.{Guid.NewGuid():N}.qscm-tmp");
        try
        {
            using (var stream = new FileStream(
                tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Same-directory publication keeps the final rename on one volume.
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Publish a new file only if no file exists at <paramref name="path"/>.
    /// The complete contents are staged first, so a loser in the create race
    /// never overwrites the winner and never exposes a partial destination.</summary>
    public static bool TryCreate(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(path);
        var tmp = Path.Combine(directory ?? "", $".{fileName}.{Guid.NewGuid():N}.qscm-tmp");
        try
        {
            using (var stream = new FileStream(
                tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tmp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
