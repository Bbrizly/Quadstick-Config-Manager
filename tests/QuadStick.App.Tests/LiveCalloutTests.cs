using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Row lighting shipped for the list view first, and device view is the one the
// app opens in, so a light only the list had was half a feature. These pin the
// other half: the callout row on the diagram lights while the QuadStick sends
// that row's output, and a gesture with nothing mapped to it never does.
public sealed class LiveCalloutTests
{
    // Lip is bound, side tube sip is bound, side tube puff is left empty on
    // purpose: the empty one is what proves an unmapped gesture cannot light.
    const string Profile =
        "Profile Name,,Solo\n" + "game.csv\n" + "Outputs,Function,usb\n" +
        "square,normal,lip\n" +
        "circle,normal,right_sip\n";

    static MainWindow Open()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.Load(Profile));
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();
        return w;
    }

    static LiveState Sending(params string[] outputs) =>
        new(0, 0, Array.Empty<int>(), "QuadStick", new HashSet<string>(outputs), true);

    [AvaloniaFact]
    public void ACalloutRowLightsWhileItsOutputIsSent()
    {
        var w = Open();
        Assert.Equal(0, w.LitCalloutCountForPreview());

        w.ShowLiveInputForPreview(Sending("square"));
        w.UpdateLayout();
        Assert.Equal(1, w.LitCalloutCountForPreview());

        w.ShowLiveInputForPreview(Sending("square", "circle"));
        w.UpdateLayout();
        Assert.Equal(2, w.LitCalloutCountForPreview());
        w.Close();
    }

    [AvaloniaFact]
    public void ACalloutRowGoesOutWhenTheOutputStops()
    {
        var w = Open();
        w.ShowLiveInputForPreview(Sending("square"));
        w.UpdateLayout();
        Assert.Equal(1, w.LitCalloutCountForPreview());

        w.ShowLiveInputForPreview(null);
        w.UpdateLayout();
        Assert.Equal(0, w.LitCalloutCountForPreview());
        w.Close();
    }

    [AvaloniaFact]
    public void AnUnmappedGestureNeverLights()
    {
        var w = Open();
        // No row carries this output, so nothing on the diagram may light.
        w.ShowLiveInputForPreview(Sending("triangle"));
        w.UpdateLayout();
        Assert.Equal(0, w.LitCalloutCountForPreview());
        w.Close();
    }
}
