using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

public class WriteAtomicTests
{
    [Fact]
    public void Writes_new_file_and_replaces_existing_without_leftover_temp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qscm-atomic-{Guid.NewGuid():N}.csv");
        try
        {
            ProfileFile.WriteAtomic(path, "first");
            Assert.Equal("first", File.ReadAllText(path));

            ProfileFile.WriteAtomic(path, "second");
            Assert.Equal("second", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".qscm-tmp"));
        }
        finally { File.Delete(path); }
    }
}
