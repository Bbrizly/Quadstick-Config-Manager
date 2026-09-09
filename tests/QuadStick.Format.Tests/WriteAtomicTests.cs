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
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".qscm-tmp*"));
        }
        finally { File.Delete(path); }
    }

    // Two saves of one file at once. The temp name used to be one fixed name
    // per target, so each write filled and moved the same temp and the file
    // that landed was neither of them. It is one of two now, every time.
    [Fact]
    public async Task Two_writes_at_once_leave_one_whole_file_and_no_leftovers()
    {
        var dir = Directory.CreateTempSubdirectory("qscm-atomic-").FullName;
        var path = Path.Combine(dir, "profile.csv");
        try
        {
            var a = new string('a', 400_000);
            var b = new string('b', 400_000);
            for (var round = 0; round < 20; round++)
            {
                await Task.WhenAll(
                    Task.Run(() => ProfileFile.WriteAtomic(path, a)),
                    Task.Run(() => ProfileFile.WriteAtomic(path, b)));

                var landed = File.ReadAllText(path);
                Assert.True(landed == a || landed == b, "a torn file landed");
                Assert.Empty(Directory.GetFiles(dir, "*.qscm-tmp*"));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
