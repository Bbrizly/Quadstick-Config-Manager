namespace QuadStick.Infrastructure.Files;

/// <summary>Crash-safe best-effort atomic replacement for ordinary local files.</summary>
public static class AtomicFileWriter
{
    public static void Write(string path, string text)
    {
        var tmp = path + ".qscm-tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // A full disk or a file held open can leave the temporary sibling.
            // Cleanup must never hide the real write failure.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
