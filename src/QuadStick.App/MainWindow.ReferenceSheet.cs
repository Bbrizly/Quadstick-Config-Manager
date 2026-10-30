using System.Globalization;
using Avalonia.Platform.Storage;
using QuadStick.Format;

namespace QuadStick.App;

public partial class MainWindow
{
    /// <summary>Asks where to keep the sheet, then writes and opens it. Avalonia
    /// has no print API, so the browser does the printing. The picker is on
    /// purpose: the sandboxed Mac build can only write where somebody chose, and
    /// a clinician gets a file to keep.</summary>
    async Task PrintReferenceSheetAsync()
    {
        if (_file is null) { Status(Strings.Main_OpenOrCreateAProfile); return; }

        var pick = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Sheet_SaveTitle,
            SuggestedFileName = BareName(SafeFileName.ForCsv(_file.Document.CsvFileName)) + "-reference-sheet",
            DefaultExtension = "html",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(Strings.Sheet_HtmlFileType)
                {
                    Patterns = new[] { "*.html" },
                    AppleUniformTypeIdentifiers = new[] { "public.html" },
                    MimeTypes = new[] { "text/html" },
                },
            },
        });
        if (pick is null) return;
        // Type a bare name in the macOS save panel and it can hand the path back
        // without the extension, and a file with none does not open in a browser.
        var path = pick.Path.LocalPath;
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) path += ".html";
        if (Path.GetDirectoryName(path) is { } dir && Device.IsInstallTarget(dir))
        {
            Status(Strings.Main_ThatFolderIsAQuadStick, StatusKind.Warning);
            return;
        }
        await WriteAndOpenReferenceSheetAsync(path);
    }

    // Reads the live document, so edits not saved yet are on the sheet. Not
    // WriteAtomic: its temp file beside the target is exactly what the sandbox
    // refuses, and a half written sheet costs nothing to write again.
    internal async Task WriteAndOpenReferenceSheetAsync(string path)
    {
        if (_file is null) { Status(Strings.Main_OpenOrCreateAProfile); return; }

        var html = ReferenceSheet.Build(_file.Document, DateTime.Now, _labelStyle == 2);
        try { await File.WriteAllTextAsync(path, html); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Sheet_CouldNotSave, path, ex.Message), StatusKind.Error);
            return;
        }

        bool opened;
        try { opened = await Launcher.LaunchFileInfoAsync(new FileInfo(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { opened = false; }
        Status(string.Format(CultureInfo.CurrentCulture, opened ? Strings.Sheet_SavedAndOpened : Strings.Sheet_SavedNotOpened, path),
            opened ? StatusKind.Ready : StatusKind.Warning);
    }
}
