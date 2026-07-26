using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Right click a profile on Home to delete it. The only way to remove one used
// to be Finder, which meant leaving the app to do it.
public class DeleteProfileTests
{
    // The card menu is built per card, so the item has to name its own file or
    // a screen reader user cannot tell which one they are about to delete.
    [AvaloniaFact]
    public void A_library_card_offers_to_delete_that_file()
    {
        var lib = Path.Combine(Path.GetTempPath(), "qcm-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lib);
        var path = Path.Combine(lib, "mygame.csv");
        File.WriteAllText(path, ProfileFile.NewFromTemplate("mygame.csv").ToCsvText());
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = lib;
        try
        {
            var s = Settings.Load();
            s.TutorialSeen = true;
            s.RememberWindow = false;
            Settings.Save(s);
            var w = new MainWindow();
            w.Show();
            w.ShowHomeForPreview();
            Dispatcher.UIThread.RunJobs();

            var card = w.GetVisualDescendants().OfType<WrapPanel>()
                .First(p => p.Name == "LibraryCards")
                .GetVisualDescendants().OfType<Button>().First();
            var items = ((MenuFlyout)card.ContextFlyout!).Items.OfType<MenuItem>().ToList();

            var del = Assert.Single(items, i => (i.Header as string) == "Delete profile");
            Assert.Equal("Delete mygame from your profile library", AutomationProperties.GetName(del));
            w.Close();
        }
        finally { MainWindow.LibraryDir = old; Directory.Delete(lib, recursive: true); }
    }
}
