using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The agent is off for this release. These pin "off", not "gone": the window,
// the guide and the bridge are all still compiled and still tested, and this
// file stops the buttons coming back by accident. Flip AgentFeature.Enabled and
// these are the tests to invert.
public class AgentOffTests
{
    static MainWindow Open()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.UpdateLayout();
        return w;
    }

    [AvaloniaFact]
    public void NeitherAgentButtonIsReachable()
    {
        var w = Open();
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close waits on the save dialog forever
        w.LoadProfile(file);
        w.UpdateLayout();

        Assert.False(AgentFeature.Enabled);
        Assert.False(w.FindControl<Button>("HomeAgentButton")!.IsVisible);
        Assert.False(w.FindControl<Button>("AgentButton")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void ShowAgentOpensNothingWhileItIsOff()
    {
        var w = Open();
        w.ShowAgent();
        w.ShowAgent(changing: true);
        Assert.Empty(w.OwnedWindows);
        w.Close();
    }

    // The point of switching off instead of deleting: the code is still here.
    [AvaloniaFact]
    public void TheAgentWindowStillBuildsAndOpens()
    {
        var w = Open();
        var agent = new AgentWindow(w, root: "/nowhere");
        Assert.NotNull(agent);
        w.Close();
    }
}
