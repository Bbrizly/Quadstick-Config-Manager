using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Changing the language must not cost the person a restart or their work. The
// window is rebuilt in the new language and the open profile crosses over as
// the same object, unsaved edits and all.
public class LanguageSwitchTests
{
    static ProfileFile Solo() => ProfileFile.Load(
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "mouse_left,normal,lip\n");

    [AvaloniaFact]
    public void Changing_language_rebuilds_the_window_and_keeps_the_profile()
    {
        var uiWas = CultureInfo.CurrentUICulture;
        var defWas = CultureInfo.DefaultThreadCurrentUICulture;
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Language = Localization.FollowSystem;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = Solo();
        w.LoadProfile(file);
        file.Dirty = true; // unsaved work is exactly what must survive

        var labelBefore = PreferenceCatalog.All[0].Label;
        // Zone 1, not 0: French for "Joystick" is "Joystick".
        var zoneBefore = MainWindow.AllZones[1].Title;
        MainWindow? next = null;
        try
        {
            next = w.SetLanguage("fr");
            var fr = CultureInfo.GetCultureInfo("fr");

            Assert.NotSame(w, next);
            Assert.False(w.IsVisible); // the old window is gone, not lingering
            Assert.Same(file, next.OpenFile); // the same object, not a copy
            Assert.True(next.OpenFile!.Dirty); // edits crossed over unsaved

            // The new window baked its text in the new language.
            Assert.StartsWith(
                Strings.ResourceManager.GetString("Main_QuadstickConfigManagerUnofficial2", fr)!,
                next.Title);
            // And the statics that cache words were rebuilt, not left behind:
            // the catalog answers by name with the retranslated entry.
            Assert.True(PreferenceCatalog.TryGet(PreferenceCatalog.All[0].Name, out var relooked));
            Assert.Equal(PreferenceCatalog.All[0].Label, relooked!.Label);
            Assert.NotEqual(labelBefore, PreferenceCatalog.All[0].Label);
            Assert.NotEqual(zoneBefore, MainWindow.AllZones[1].Title);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = defWas;
            CultureInfo.CurrentUICulture = uiWas;
            Localization.Relocalize(); // statics back in the suite's language
            var s2 = Settings.Load();
            s2.Language = Localization.FollowSystem;
            Settings.Save(s2);
            file.Dirty = false; // let the window close without asking
            next?.Close();
        }
    }

    // Reported by a German user: Settings, change language, Back, and the
    // profile was gone behind Home. The rebuild opened Settings twice, and
    // the second open recorded Settings itself as the page to return to.
    [AvaloniaFact]
    public void Leaving_settings_after_a_language_change_returns_to_the_profile()
    {
        var uiWas = CultureInfo.CurrentUICulture;
        var defWas = CultureInfo.DefaultThreadCurrentUICulture;
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Language = Localization.FollowSystem;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = Solo();
        w.LoadProfile(file);
        MainWindow? next = null;
        w.EditorReplaced += n => next = n;
        try
        {
            w.ShowSettingsPage();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            var language = w.GetVisualDescendants().OfType<ComboBox>().Single(c =>
                AutomationProperties.GetName(c) == Strings.Settings_LanguageHelp);
            language.SelectedIndex = Localization.IndexOf("de"); // the real control, not SetLanguage

            Assert.NotNull(next);
            Assert.True(next!.SettingsPage.IsVisible);
            next.LeaveSettingsPage();

            Assert.True(next.EditorView.IsVisible);
            Assert.Same(file, next.OpenFile);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = defWas;
            CultureInfo.CurrentUICulture = uiWas;
            Localization.Relocalize();
            var s2 = Settings.Load();
            s2.Language = Localization.FollowSystem;
            Settings.Save(s2);
            (next ?? w).Close();
        }
    }

    [AvaloniaFact]
    public void Picking_the_language_already_set_changes_nothing()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.Language = "fr";
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        try
        {
            Assert.Same(w, w.SetLanguage("fr"));
        }
        finally
        {
            var s2 = Settings.Load();
            s2.Language = Localization.FollowSystem;
            Settings.Save(s2);
            w.Close();
        }
    }
}
