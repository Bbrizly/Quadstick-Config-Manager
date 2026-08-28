namespace QuadStick.App;

/// <summary>Filesystem port used by application workflows that manage the local
/// profile library. Paths are opaque to the implementation's callers.</summary>
public interface IProfileLibraryStore
{
    bool Exists(string path);
    IReadOnlyList<string> ListCsvFiles(string directory);
    void EnsureDirectory(string directory);
    string ReadText(string path);
    void WriteAtomic(string path, string text);
    bool TryCreate(string path, string text);
    void Delete(string path);
}
