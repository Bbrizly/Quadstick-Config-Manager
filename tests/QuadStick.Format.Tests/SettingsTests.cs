using QuadStick.App;
using Xunit;

public class SettingsTests
{
    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "settings.json");

        var settings = new AppSettings
        {
            Model = "Singleton",
            Theme = "Dark",
            InterfaceScalePercent = 150,
            ReduceMotion = true,
            RememberWindow = false,
            TutorialSeen = true,
            WinW = 1024.5,
            WinH = 768.25,
            WinX = 12.0,
            WinY = 34.0,
        };

        Settings.Save(settings, path);
        var loaded = Settings.Load(path);

        Assert.Equal("Singleton", loaded.Model);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal(150, loaded.InterfaceScalePercent);
        Assert.True(loaded.ReduceMotion);
        Assert.False(loaded.RememberWindow);
        Assert.True(loaded.TutorialSeen);
        Assert.Equal(1024.5, loaded.WinW);
        Assert.Equal(768.25, loaded.WinH);
        Assert.Equal(12.0, loaded.WinX);
        Assert.Equal(34.0, loaded.WinY);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = Settings.Load(Path.Combine(Path.GetTempPath(), "nope-xyz.json"));

        Assert.Equal("FPS", settings.Model);
        Assert.Equal("System", settings.Theme);
        Assert.Equal(100, settings.InterfaceScalePercent);
        Assert.False(settings.ReduceMotion);
        Assert.True(settings.RememberWindow);
        Assert.False(settings.TutorialSeen);
        Assert.Null(settings.WinW);
        Assert.Null(settings.WinH);
        Assert.Null(settings.WinX);
        Assert.Null(settings.WinY);
    }

    [Fact]
    public void Load_OldFormatFile_ReadsModelAndThemeNewKeysDefault()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "settings.json");
        Settings.WriteRaw(path, "{\"model\":\"Nexus\",\"theme\":\"Dark\"}");

        var settings = Settings.Load(path);

        Assert.Equal("Nexus", settings.Model);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(100, settings.InterfaceScalePercent);
        Assert.False(settings.ReduceMotion);
        Assert.True(settings.RememberWindow);
        Assert.False(settings.TutorialSeen);
        Assert.Null(settings.WinW);
        Assert.Null(settings.WinH);
        Assert.Null(settings.WinX);
        Assert.Null(settings.WinY);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "settings.json");
        Settings.WriteRaw(path, "not json");

        var settings = Settings.Load(path);

        Assert.Equal("FPS", settings.Model);
        Assert.Equal("System", settings.Theme);
        Assert.Equal(100, settings.InterfaceScalePercent);
        Assert.False(settings.ReduceMotion);
        Assert.True(settings.RememberWindow);
        Assert.False(settings.TutorialSeen);
        Assert.Null(settings.WinW);
        Assert.Null(settings.WinH);
        Assert.Null(settings.WinX);
        Assert.Null(settings.WinY);
    }
}
