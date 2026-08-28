namespace QuadStick.Application.Profiles;

/// <summary>Filesystem port used by application workflows that manage the local
/// profile library. Paths identify files in the user's local library, never a
/// device transport.</summary>
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
