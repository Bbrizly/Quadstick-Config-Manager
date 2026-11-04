using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using Xunit;
using Xunit.Abstractions;

namespace QuadStick.App.Tests;

// The real wire: this app's RemoteSession and WebSocketTransport as the
// patient, the clinic's own session.ts as the clinician (run by node), through
// a real relay. Off unless QCM_RELAY_INTEROP names one, for example
//
//   (cd ../clinic-remote/relay && npx wrangler dev --port 8787 --ip 127.0.0.1)
//   QCM_RELAY_INTEROP=ws://127.0.0.1:8787 \
//   QCM_INTEROP_CLINICIAN=../clinic-remote/relay/scripts/interop-clinician.mjs \
//   dotnet test --filter RemoteInteropTests
//
// Without them the test returns at once and passes, so the normal suite never
// needs a network.
public class RemoteInteropTests : IDisposable
{
    readonly ITestOutputHelper output;

    const string Profile =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "square,normal,lip\n" +
        "circle,normal,right_sip\n";

    const string Changed =
        "Profile Name,,Solo\n" +
        "game.csv\n" +
        "Outputs,Function,usb\n" +
        "square,normal,lip\n" +
        "cross,normal,right_sip\n";

    readonly string _dir = Directory.CreateTempSubdirectory("qscm-interop-").FullName;
    readonly string _wasLibrary = MainWindow.LibraryDir;

    public RemoteInteropTests(ITestOutputHelper output)
    {
        this.output = output;
        MainWindow.LibraryDir = Path.Combine(_dir, "library");
        CrashGuard.RescueDirOverride = Path.Combine(_dir, "rescue");
    }

    public void Dispose()
    {
        MainWindow.LibraryDir = _wasLibrary;
        CrashGuard.RescueDirOverride = null;
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task The_clinics_session_and_this_one_talk_through_a_real_relay()
    {
        var relay = Environment.GetEnvironmentVariable("QCM_RELAY_INTEROP");
        var script = Environment.GetEnvironmentVariable("QCM_INTEROP_CLINICIAN");
        if (string.IsNullOrEmpty(relay) || string.IsNullOrEmpty(script)) return;
        script = Path.GetFullPath(script);

        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow { FindDeviceRoots = Array.Empty<string> };
        w.Show();
        var path = Path.Combine(_dir, "game.csv");
        File.WriteAllText(path, Profile);
        w.OpenPath(path);

        int asks = 0;
        w.AskRemoteProposalOverride = _ => Task.FromResult(++asks == 2); // decline, then accept
        w.OfferRemoteInstallOverride = () => Task.FromResult(false);

        var code = RemoteCrypto.NewCode();
        using var socket = new WebSocketTransport();
        var session = new RemoteSession(w, socket, code)
        {
            LiveSource = () => new LiveState(0.25, 0, new[] { 1 }, "QuadStick",
                new HashSet<string>(new[] { "square" }, StringComparer.Ordinal), true),
        };
        var run = session.RunAsync(new Uri(relay));
        for (int i = 0; i < 250 && session.Status == RemoteStatus.Connecting; i++) await Task.Delay(20);
        Assert.True(session.Status == RemoteStatus.Waiting, session.EndReason);

        var psi = new ProcessStartInfo("node", $"\"{script}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        psi.Environment["RELAY"] = relay;
        psi.Environment["CODE"] = code;
        psi.Environment["CSV"] = Profile;
        psi.Environment["REV"] = RemoteCrypto.Rev(Profile);
        psi.Environment["CHANGED"] = Changed;
        psi.Environment["REV2"] = RemoteCrypto.Rev(Changed);
        using var node = Process.Start(psi)!;
        var stdout = node.StandardOutput.ReadToEndAsync();
        var stderr = node.StandardError.ReadToEndAsync();
        var exited = node.WaitForExitAsync();

        bool sawConnected = false;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!exited.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (session.Status == RemoteStatus.Connected) sawConnected = true;
            if (session.LiveOn) session.PumpLive(); // headless timers do not tick on their own
            if (session.PendingProposal is { IsCompleted: false } p) await p;
            await Task.Delay(20);
        }
        if (!exited.IsCompleted) node.Kill();
        await exited;

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();
        var report = string.Join("\n", lines.Select(l => l.ToJsonString())) + "\n" + await stderr;
        output.WriteLine(report);

        Assert.True(node.ExitCode == 0, report);
        Assert.Equal(new[] { "connected", "profile.get", "propose stale", "propose declined", "propose accepted", "live", "bye" },
            lines.Select(l => (string?)l["step"]));
        Assert.All(lines, l => Assert.True((bool)l["ok"]!, report));

        // The patient side saw it too: connected, two asks, the accepted file
        // written with a rescue copy of the old one, and the clinician's bye.
        Assert.True(sawConnected);
        Assert.Equal(2, asks);
        Assert.Equal(Changed, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "rescue")));
        for (int i = 0; i < 100 && session.Status != RemoteStatus.Ended; i++) await Task.Delay(20);
        Assert.Equal(RemoteStatus.Ended, session.Status);
        await run;
    }
}
