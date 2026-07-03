using QuadStick.App;
using Xunit;

public class SettingsTests
{
    [Fact]
    public void SaveTheme_PreservesModel()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "settings.json");
        Settings.WriteRaw(path, "{\"model\":\"Singleton\"}");

        Settings.Save(path, model: null, theme: "Dark");   // only theme changes
        var (model, theme) = Settings.Load(path);

        Assert.Equal("Singleton", model);
        Assert.Equal("Dark", theme);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var (model, theme) = Settings.Load(Path.Combine(Path.GetTempPath(), "nope-xyz.json"));
        Assert.Equal("FPS", model);
        Assert.Equal("System", theme);
    }
}
