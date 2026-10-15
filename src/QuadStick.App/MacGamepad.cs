using System.Diagnostics;

namespace QuadStick.App;

// SDL, which many Mac games read controllers through, only takes a device as a
// gamepad if it knows its layout, and no SDL build knows the QuadStick's. This
// hands SDL the layout through SDL_GAMECONTROLLERCONFIG for every app launched
// afterwards, and a LaunchAgent sets it again at each login. No entitlement and
// no virtual device: the stick stays in mode 0 with its keyboard, mouse and drive.
public static class MacGamepad
{
    public const string Label = "com.bbrizly.quadstickconfigmanager.sdl-gamepad";

    // Mode 0, 16D0:092B. The layout is FW 2373 Descriptors.h:85 (buttons 1-13
    // square, cross, circle, triangle, L1, R1, L2, R2, select, start, L3, R3, PS;
    // a hat; X, Y, Z, Rz), and SDL's IOKit backend sorts elements by usage, so
    // this is the same line the community DB has for the Horipad 4 PS3. The
    // second GUID carries the device's bcdDevice (0x0003) for SDL builds that
    // only match exactly.
    const string Layout = "a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,"
        + "leftshoulder:b4,leftstick:b10,lefttrigger:b6,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,"
        + "righttrigger:b7,rightx:a2,righty:a3,start:b9,x:b0,y:b3,platform:Mac OS X,";
    public static readonly string[] Mappings =
    [
        "03000000d01600002b09000000000000,QuadStick," + Layout,
        "03000000d01600002b09000003000000,QuadStick," + Layout,
    ];

    // The App Store build is sandboxed, and the sandbox refuses both launchctl
    // and ~/Library/LaunchAgents, so there the user runs Command themselves.
    internal static bool Sandboxed = Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID") is not null;

    // ponytail: overwrites an SDL_GAMECONTROLLERCONFIG the user set by hand;
    // merge with `launchctl getenv` if anyone turns out to have one.
    public static string Command =>
        "mkdir -p ~/Library/LaunchAgents && /usr/bin/plutil -convert xml1 -o ~/Library/LaunchAgents/" + Label
        + ".plist - <<< '{\"Label\":\"" + Label + "\",\"ProgramArguments\":[\"/bin/launchctl\",\"setenv\","
        + "\"SDL_GAMECONTROLLERCONFIG\",\"" + string.Join("\\n", Mappings) + "\"],\"RunAtLoad\":true}'"
        + " && launchctl setenv SDL_GAMECONTROLLERCONFIG $'" + string.Join("\\n", Mappings) + "'";

    public static string UndoCommand =>
        "launchctl unsetenv SDL_GAMECONTROLLERCONFIG; rm -f ~/Library/LaunchAgents/" + Label + ".plist";

    public static string AgentPath => Path.Combine(
        TestEnvironment?.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    public static bool IsOn => File.Exists(AgentPath);

    // Tests run the real command with HOME and PATH pointed at a scratch folder.
    internal static Dictionary<string, string>? TestEnvironment;

    /// <summary>Runs one of the two commands in zsh, Terminal's own shell.</summary>
    public static bool Run(string command, string shell = "/bin/zsh")
    {
        try
        {
            // Output is not redirected: nothing reads it, and an unread pipe can
            // fill and stall the shell.
            var psi = new ProcessStartInfo(shell, ["-c", command]) { UseShellExecute = false };
            foreach (var (k, v) in TestEnvironment ?? []) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(10_000))
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
