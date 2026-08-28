namespace QuadStick.App;

// Persisted application state. This lives outside the Avalonia project so
// application workflows and infrastructure persistence can share the model
// without depending on presentation code. The namespace stays unchanged during
// the migration to preserve source and serialized compatibility.
public sealed class AppSettings
{
    public string Model = "FPS";
    public string Theme = "System";           // System | Light | Dark
    public int InterfaceScalePercent = 100;    // 100 | 125 | 150 | 200
    public bool ReduceMotion = false;
    public bool RememberWindow = true;
    public bool TutorialSeen = false;
    public bool DeviceCards = true;
    public string PickerGrouping = "Detailed"; // Detailed | Wide | Flat
    public double? WinW, WinH, WinX, WinY;
    public bool DriveBackup = true;
    public Dictionary<string, DriveLink> DriveLinks = new();
    public List<string> Recents = new();
    public Dictionary<string, Dictionary<string, string>> CustomNames = new();
    public int TelemetryNoticeVersion = 0;
    public bool UsageAnalytics = false;
    public bool AskAboutCrashes = true;
    public string InstallId = "";
}

// Per-profile remote-backup state, keyed by local profile path.
public sealed class DriveLink
{
    public string SpreadsheetId = "";
    public string LastSeenModifiedTime = "";
    public bool BackupDirty = false;
    public bool LinkShared = false;
}
