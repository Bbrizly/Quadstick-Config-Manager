using System.Diagnostics;
using System.Text.Json;

namespace QuadStick.App;

// Runs qs-bridge (crates/qs-bridge) for as long as the switch is on and the
// app is open, the way QMP runs its ViGEm bridge on Windows. A child process
// and not a library: a crash in either cannot take the other down, and the
// Tauri app runs the same binary the same way.
//
// The bridge stops when its stdin closes, and it releases every button on the
// virtual pad before it goes. So closing stdin is the stop, and it also fires
// when this app dies without a chance to say anything.
public sealed class ControllerBridge : IDisposable
{
    public enum State { Waiting, Bridging, NoAccess, NoVirtualPad, Unsupported, Stopped }

    // Tests point this at a stand-in script. Null means look beside the app.
    internal static string? PathOverride;

    /// <summary>The bridge binary shipped beside the app, or null.</summary>
    public static string? FindBinary()
    {
        if (PathOverride is not null) return PathOverride;
        var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "qs-bridge.exe" : "qs-bridge");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Whether to offer the switch at all. Only Linux can make the
    /// virtual pad so far; macOS waits on Apple's virtual HID entitlement.</summary>
    public static bool Offered => PathOverride is not null || (OperatingSystem.IsLinux() && FindBinary() is not null);

    /// <summary>One status line from <c>qs-bridge --json</c>. Null for anything
    /// else, so a newer bridge with a new status is ignored, not misread.</summary>
    public static State? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String) return null;
            return s.GetString() switch
            {
                "waiting" => State.Waiting,
                "bridging" => State.Bridging,
                "no_access" => State.NoAccess,
                "no_virtual_pad" => State.NoVirtualPad,
                "unsupported" => State.Unsupported,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    readonly Process _process;
    readonly Action<State> _onState;
    volatile bool _stopping;
    State? _last;

    /// <summary>Starts the bridge. <paramref name="onState"/> runs on a
    /// background thread; the caller moves it to the UI thread.</summary>
    public ControllerBridge(string path, Action<State> onState)
    {
        _onState = onState;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(path, "--json")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        _process.OutputDataReceived += (_, e) =>
        {
            if (Parse(e.Data) is not { } state) return;
            _last = state;
            _onState(state);
        };
        // Its stderr is for a person running it by hand. Read and dropped, so
        // a full pipe can never block the bridge mid-game.
        _process.ErrorDataReceived += (_, _) => { };
        _process.Exited += (_, _) =>
        {
            // Exited can beat the last status line; this wait drains the output
            // first, so "unsupported" is not lost to it. Bounded, because a pipe
            // held open by anything else would otherwise park this forever.
            _process.WaitForExit(1000);
            if (!_stopping && _last != State.Unsupported) _onState(State.Stopped);
        };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                // It checks for the close four times a second.
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        _process.Dispose();
    }
}
