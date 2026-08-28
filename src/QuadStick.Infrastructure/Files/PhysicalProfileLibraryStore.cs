using QuadStick.Application.Profiles;

namespace QuadStick.Infrastructure.Files;

/// <summary>Physical filesystem implementation of the local profile-library port.</summary>
public sealed class PhysicalProfileLibraryStore : IProfileLibraryStore
{
    public bool Exists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListCsvFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.csv")
            : Array.Empty<string>();

    public void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);

    public string ReadText(string path) => File.ReadAllText(path);

    public void WriteAtomic(string path, string text) => AtomicFileWriter.Write(path, text);

    public bool TryCreate(string path, string text) => AtomicFileWriter.TryCreate(path, text);

    public void Delete(string path) => File.Delete(path);
}
