using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The patient side of a clinician session, driven through a fake socket. The
// fake clinician seals frames with the same code, so every test exercises the
// real cipher, the real seq rule, and the real editor behind them.
public class RemoteSessionTests : IDisposable
{
    const string Code = "K7QM4XPA9RTD";

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

    readonly string _dir = Directory.CreateTempSubdirectory("qscm-remote-").FullName;
    readonly string _wasLibrary = MainWindow.LibraryDir;

    public RemoteSessionTests()
    {
        MainWindow.LibraryDir = Path.Combine(_dir, "library");
        CrashGuard.RescueDirOverride = Path.Combine(_dir, "rescue");
    }

    public void Dispose()
    {
        MainWindow.LibraryDir = _wasLibrary;
        CrashGuard.RescueDirOverride = null;
        GC.SuppressFinalize(this);
    }

    // ---- the fake wire ----

    sealed class FakeSocket : IRemoteSocket
    {
        public readonly List<string> Sent = new();
        public readonly Channel<string?> Incoming = Channel.CreateUnbounded<string?>();
        public Exception? ConnectThrows;
        public bool ConnectHangs;
        public bool Closed;
        public int? CloseCode { get; set; }
        public Uri? ConnectedTo;

        public async Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectedTo = uri;
            if (ConnectHangs) await Task.Delay(Timeout.Infinite, ct);
            if (ConnectThrows is { } ex) throw ex;
        }
        public Task SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await Incoming.Reader.ReadAsync(ct);
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
        public void Dispose() { }
    }

    // The other end, with its own seq.
    sealed class Clinician(string code, string from = "clinician")
    {
        readonly string _room = RemoteCrypto.Room(code);
        readonly byte[] _key = RemoteCrypto.Key(code, RemoteCrypto.Room(code));
        long _seq;

        public string Frame(string type, JsonObject? body = null, long? seq = null)
        {
            var msg = new JsonObject { ["v"] = 1, ["seq"] = seq ?? ++_seq, ["type"] = type };
            if (from.Length > 0) msg["from"] = from;
            if (body is not null) foreach (var (k, v) in body.ToList()) { body.Remove(k); msg[k] = v; }
            return RemoteCrypto.Seal(_key, _room, msg.ToJsonString());
        }

        public JsonObject Read(string frame) =>
            (JsonObject)JsonNode.Parse(RemoteCrypto.Open(_key, _room, frame) ?? throw new Exception("no decrypt"))!;
    }

    sealed class Rig
    {
        public MainWindow Window = null!;
        public FakeSocket Socket = new();
        public RemoteSession Session = null!;
        public Clinician Clinician = new(Code);
        public Task Run = Task.CompletedTask;

        public List<JsonObject> Sent => Socket.Sent.Select(Clinician.Read).ToList();
        public JsonObject Last => Sent[^1];
        public Task Say(string type, JsonObject? body = null) => Session.HandleFrameAsync(Clinician.Frame(type, body));
    }

    static MainWindow NewWindow(Func<IReadOnlyList<string>>? roots = null)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow { FindDeviceRoots = roots ?? Array.Empty<string> };
        w.Show();
        return w;
    }

    string SavedProfile(string text = Profile)
    {
        var path = Path.Combine(_dir, "game.csv");
        File.WriteAllText(path, text);
        return path;
    }

    static async Task<Rig> Connected(MainWindow? window = null)
    {
        var r = new Rig { Window = window ?? NewWindow() };
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        r.Run = r.Session.RunAsync(new Uri("wss://relay.test"));
        await r.Say("hello", new JsonObject { ["role"] = "clinician", ["app"] = "Clinic", ["version"] = "1" });
        Assert.Equal(RemoteStatus.Connected, r.Session.Status);
        return r;
    }

    // ---- crypto ----

    sealed record Vector(string Code, string Room, string KeyHex, string NonceHex, string Plaintext, string Frame);

    static IEnumerable<Vector> Vectors([CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "specs", "remote-vectors.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!["vectors"]!.AsArray();
        return json.Select(v => new Vector((string)v!["code"]!, (string)v["room"]!, (string)v["keyHex"]!,
            (string)v["nonceHex"]!, (string)v["plaintext"]!, (string)v["frame"]!));
    }

    [Fact]
    public void Every_shared_vector_is_reproduced_exactly()
    {
        var all = Vectors().ToList();
        Assert.Equal(2, all.Count);
        foreach (var v in all)
        {
            Assert.Equal(v.Room, RemoteCrypto.Room(v.Code));
            var key = RemoteCrypto.Key(v.Code, v.Room);
            Assert.Equal(v.KeyHex, Convert.ToHexString(key).ToLowerInvariant());
            Assert.Equal(v.Plaintext, RemoteCrypto.Open(key, v.Room, v.Frame));
            Assert.Equal(v.Frame, RemoteCrypto.Seal(key, v.Room, v.Plaintext, Convert.FromHexString(v.NonceHex)));
        }
    }

    [Fact]
    public void A_frame_does_not_open_with_the_wrong_room_key_or_a_flipped_bit()
    {
        var v = Vectors().First();
        var key = RemoteCrypto.Key(v.Code, v.Room);
        Assert.Null(RemoteCrypto.Open(key, "00000000000000000000000000000000", v.Frame));
        Assert.Null(RemoteCrypto.Open(RemoteCrypto.Key("0123456789AB", v.Room), v.Room, v.Frame));
        var flipped = (v.Frame[20] == 'A' ? 'B' : 'A') + "";
        Assert.Null(RemoteCrypto.Open(key, v.Room, v.Frame[..20] + flipped + v.Frame[21..]));
        Assert.Null(RemoteCrypto.Open(key, v.Room, "not base64 !"));
        Assert.Null(RemoteCrypto.Open(key, v.Room, "AAAA"));
    }

    [Fact]
    public void A_code_is_twelve_crockford_characters_shown_in_threes_of_four()
    {
        for (int i = 0; i < 50; i++)
        {
            var c = RemoteCrypto.NewCode();
            Assert.Equal(12, c.Length);
            Assert.All(c, ch => Assert.Contains(ch, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));
        }
        Assert.Equal("K7QM-4XPA-9RTD", RemoteCrypto.Display(Code));
        Assert.NotEqual(RemoteCrypto.NewCode(), RemoteCrypto.NewCode());
    }

    [Theory]
    [InlineData("k7qm-4xpa-9rtd", "K7QM4XPA9RTD")]
    [InlineData(" K7QM 4XPA 9RTD ", "K7QM4XPA9RTD")]
    [InlineData("O1IL-0000-0000", "0111" + "00000000")]
    [InlineData("K7QM-4XPA-9RT", null)]
    [InlineData("K7QM-4XPA-9RTU", null)]
    [InlineData("", null)]
    public void A_typed_code_is_normalized_or_refused(string typed, string? expected) =>
        Assert.Equal(expected, RemoteCrypto.Normalize(typed));

    [Fact]
    public void The_relay_comes_from_the_environment_then_the_setting_then_the_default()
    {
        var was = Environment.GetEnvironmentVariable("QCM_RELAY_URL");
        try
        {
            Environment.SetEnvironmentVariable("QCM_RELAY_URL", null);
            Assert.Equal("wss://relay.bassamkamal.dev/", RemoteSession.RelayBase(null).ToString());
            Assert.Equal("wss://other.test/", RemoteSession.RelayBase("wss://other.test/").ToString());
            // Plain ws is for wrangler dev on this machine only.
            Assert.Equal("wss://relay.bassamkamal.dev/", RemoteSession.RelayBase("ws://evil.test").ToString());
            Environment.SetEnvironmentVariable("QCM_RELAY_URL", "ws://127.0.0.1:8787");
            Assert.Equal("ws://127.0.0.1:8787/", RemoteSession.RelayBase("wss://other.test").ToString());
        }
        finally { Environment.SetEnvironmentVariable("QCM_RELAY_URL", was); }
    }

    // ---- connecting ----

    [AvaloniaFact]
    public async Task Start_connects_to_the_room_and_says_hello_when_the_clinician_arrives()
    {
        var r = new Rig { Window = NewWindow() };
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        r.Run = r.Session.RunAsync(new Uri("wss://relay.test"));

        Assert.Equal(new Uri("wss://relay.test/r/fd206258c608e3132f9d4606dd47bd13"), r.Socket.ConnectedTo);
        Assert.Equal(RemoteStatus.Waiting, r.Session.Status);
        Assert.Empty(r.Sent); // the relay would drop a hello sent to nobody
        await r.Session.HandleFrameAsync("{\"relay\":\"peer-joined\"}");
        var hello = Assert.Single(r.Sent);
        Assert.Equal("patient", (string?)hello["from"]);
        Assert.Equal("hello", (string?)hello["type"]);
        Assert.Equal("patient", (string?)hello["role"]);
        Assert.Equal("QCM", (string?)hello["app"]);
        Assert.Equal(1, (long)hello["seq"]!);
        Assert.Equal(1, (long)hello["v"]!);
    }

    [AvaloniaFact]
    public async Task Hello_goes_once_whichever_arrives_first()
    {
        var r = new Rig { Window = NewWindow() };
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        r.Run = r.Session.RunAsync(new Uri("wss://relay.test"));
        await r.Session.HandleFrameAsync("{\"relay\":\"peer-joined\"}");
        Assert.Equal(RemoteStatus.Waiting, r.Session.Status); // not until their hello decrypts
        await r.Say("hello", new JsonObject { ["role"] = "clinician" });
        await r.Session.HandleFrameAsync("{\"relay\":\"peer-joined\"}");
        Assert.Single(r.Sent);
        Assert.Equal(RemoteStatus.Connected, r.Session.Status);
    }

    [AvaloniaFact]
    public async Task A_clinicians_hello_before_peer_joined_is_answered()
    {
        var r = await Connected();
        var hello = Assert.Single(r.Sent);
        Assert.Equal("hello", (string?)hello["type"]);
    }

    [AvaloniaFact]
    public async Task Our_own_hello_bounced_back_is_not_a_clinician()
    {
        var r = new Rig { Window = NewWindow() };
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        r.Run = r.Session.RunAsync(new Uri("wss://relay.test"));
        await r.Say("hello", new JsonObject { ["role"] = "patient" });
        Assert.Equal(RemoteStatus.Waiting, r.Session.Status);
    }

    // One key and one AAD serve both directions, so a frame of ours that the
    // relay sends back would decrypt. "from" is inside the ciphertext.
    [AvaloniaFact]
    public async Task A_reflected_or_unsigned_frame_is_dropped_and_counted_bad()
    {
        var r = await Connected();
        int n = r.Socket.Sent.Count;
        var mirror = new Clinician(Code, from: "patient");
        var unsigned = new Clinician(Code, from: "");
        await r.Session.HandleFrameAsync(mirror.Frame("bye", seq: 50));
        Assert.Equal(RemoteStatus.Connected, r.Session.Status); // a reflected bye does not end it
        await r.Session.HandleFrameAsync(unsigned.Frame("profile.get", new JsonObject { ["id"] = "x" }, seq: 51));
        Assert.Equal(n, r.Socket.Sent.Count);
        Assert.Equal(RemoteStatus.Connected, r.Session.Status);
        await r.Session.HandleFrameAsync(mirror.Frame("profile.get", new JsonObject { ["id"] = "y" }, seq: 52));
        Assert.Equal(RemoteStatus.Ended, r.Session.Status);
        Assert.Equal(Strings.Remote_CodeMismatch, r.Session.EndReason);
    }

    [AvaloniaFact]
    public async Task Everything_we_send_says_it_is_from_the_patient()
    {
        var r = await Connected();
        await r.Say("profile.get", new JsonObject { ["id"] = "a" });
        Assert.All(r.Sent, m => Assert.Equal("patient", (string?)m["from"]));
    }

    [AvaloniaFact]
    public async Task Nothing_but_hello_is_answered_before_the_clinician_says_hello()
    {
        var r = new Rig { Window = NewWindow() };
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        r.Run = r.Session.RunAsync(new Uri("wss://relay.test"));
        await r.Say("profile.get", new JsonObject { ["id"] = "a" });
        Assert.Empty(r.Sent);
    }

    [AvaloniaFact]
    public async Task A_busy_relay_says_so()
    {
        var r = new Rig { Window = NewWindow() };
        r.Socket.ConnectThrows = new RemoteConnectException(busy: true);
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        await r.Session.RunAsync(new Uri("wss://relay.test"));
        Assert.Equal(RemoteStatus.Ended, r.Session.Status);
        Assert.Equal(Strings.Remote_Busy, r.Session.EndReason);
        Assert.Empty(r.Socket.Sent);
    }

    [AvaloniaFact]
    public async Task A_relay_that_cannot_be_reached_in_time_says_what_to_try()
    {
        var r = new Rig { Window = NewWindow() };
        r.Socket.ConnectHangs = true;
        r.Session = new RemoteSession(r.Window, r.Socket, Code) { ConnectTimeout = TimeSpan.FromMilliseconds(50) };
        await r.Session.RunAsync(new Uri("wss://relay.test"));
        Assert.Equal(Strings.Remote_Unreachable, r.Session.EndReason);
        Assert.Contains("Google Sheets", Strings.Remote_Unreachable);
    }

    [AvaloniaFact]
    public async Task A_refused_connect_is_unreachable_too()
    {
        var r = new Rig { Window = NewWindow() };
        r.Socket.ConnectThrows = new System.Net.WebSockets.WebSocketException("nope");
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        await r.Session.RunAsync(new Uri("wss://relay.test"));
        Assert.Equal(Strings.Remote_Unreachable, r.Session.EndReason);
    }

    [AvaloniaTheory]
    [InlineData(4001)]
    [InlineData(4002)]
    [InlineData(4003)]
    [InlineData(4008)]
    [InlineData(1009)]
    [InlineData(1013)]
    [InlineData(1000)]
    public async Task Every_relay_close_is_said_in_words(int code)
    {
        var r = new Rig { Window = NewWindow() };
        r.Session = new RemoteSession(r.Window, r.Socket, Code);
        r.Run = r.Session.RunAsync(new Uri("wss://relay.test"));
        r.Socket.CloseCode = code;
        r.Socket.Incoming.Writer.TryWrite(null);
        await r.Run;
        var expected = code switch
        {
            4001 => Strings.Remote_RoomFull,
            4002 => Strings.Remote_Expired,
            4003 => Strings.Remote_TimeUp,
            4008 or 1009 => Strings.Remote_TooMuch,
            1013 => Strings.Remote_Busy,
            _ => Strings.Remote_Ended,
        };
        Assert.Equal(expected, r.Session.EndReason);
    }

    [AvaloniaFact]
    public async Task Frames_arriving_on_the_socket_are_handled()
    {
        var r = await Connected();
        r.Socket.Incoming.Writer.TryWrite(r.Clinician.Frame("bye"));
        await r.Run;
        Assert.Equal(RemoteStatus.Ended, r.Session.Status);
    }

    // ---- ending ----

    [AvaloniaFact]
    public async Task Peer_left_ends_the_session_for_good()
    {
        var r = await Connected();
        await r.Session.HandleFrameAsync("{\"relay\":\"peer-left\"}");
        Assert.Equal(RemoteStatus.Ended, r.Session.Status);
        Assert.Equal(Strings.Remote_Ended, r.Session.EndReason);
        Assert.True(r.Socket.Closed);
    }

    [AvaloniaFact]
    public async Task Bye_from_the_clinician_ends_it()
    {
        var r = await Connected();
        await r.Say("bye");
        Assert.Equal(RemoteStatus.Ended, r.Session.Status);
        Assert.True(r.Socket.Closed);
    }

    [AvaloniaFact]
    public async Task End_says_bye_then_closes_and_sends_nothing_after()
    {
        var r = await Connected();
        await r.Session.EndAsync();
        Assert.Equal("bye", (string?)r.Last["type"]);
        Assert.True(r.Socket.Closed);
        int n = r.Socket.Sent.Count;
        await r.Say("profile.get", new JsonObject { ["id"] = "late" });
        Assert.Equal(n, r.Socket.Sent.Count);
    }

    // ---- seq and bad frames ----

    [AvaloniaFact]
    public async Task A_replayed_or_older_frame_is_dropped()
    {
        var r = await Connected(NewWindow());
        var get = r.Clinician.Frame("profile.get", new JsonObject { ["id"] = "a" }, seq: 5);
        await r.Session.HandleFrameAsync(get);
        int n = r.Socket.Sent.Count;
        await r.Session.HandleFrameAsync(get); // the same frame again
        await r.Session.HandleFrameAsync(r.Clinician.Frame("profile.get", new JsonObject { ["id"] = "b" }, seq: 4));
        await r.Session.HandleFrameAsync(r.Clinician.Frame("profile.get", new JsonObject { ["id"] = "c" }, seq: 5));
        Assert.Equal(n, r.Socket.Sent.Count);
        await r.Session.HandleFrameAsync(r.Clinician.Frame("profile.get", new JsonObject { ["id"] = "d" }, seq: 6));
        Assert.Equal(n + 1, r.Socket.Sent.Count);
        Assert.Equal("d", (string?)r.Last["re"]);
    }

    [AvaloniaFact]
    public async Task Our_frames_count_up_from_one()
    {
        var r = await Connected();
        await r.Say("profile.get", new JsonObject { ["id"] = "a" });
        await r.Say("profile.get", new JsonObject { ["id"] = "b" });
        Assert.Equal(new long[] { 1, 2, 3 }, r.Sent.Select(m => (long)m["seq"]!));
    }

    [AvaloniaFact]
    public async Task Three_frames_that_do_not_decrypt_close_with_code_does_not_match()
    {
        var r = await Connected();
        var stranger = new Clinician("0123456789AB");
        await r.Session.HandleFrameAsync(stranger.Frame("profile.get"));
        await r.Session.HandleFrameAsync("garbage");
        Assert.Equal(RemoteStatus.Connected, r.Session.Status);
        await r.Session.HandleFrameAsync(new string('A', RemoteSession.MaxFrame + 1)); // over the cap
        Assert.Equal(RemoteStatus.Ended, r.Session.Status);
        Assert.Equal(Strings.Remote_CodeMismatch, r.Session.EndReason);
        Assert.True(r.Socket.Closed);
    }

    [AvaloniaFact]
    public async Task An_unknown_type_is_ignored()
    {
        var r = await Connected();
        int n = r.Socket.Sent.Count;
        await r.Say("cursor.move", new JsonObject { ["x"] = 1 });
        Assert.Equal(n, r.Socket.Sent.Count);
        Assert.Equal(RemoteStatus.Connected, r.Session.Status);
    }

    // ---- profile.get ----

    [AvaloniaFact]
    public async Task Get_sends_the_saved_file_and_its_rev()
    {
        var w = NewWindow();
        var path = SavedProfile();
        w.OpenPath(path);
        var r = await Connected(w);
        await r.Say("profile.get", new JsonObject { ["id"] = "g1" });

        var m = r.Last;
        Assert.Equal("profile", (string?)m["type"]);
        Assert.Equal("g1", (string?)m["re"]);
        Assert.Equal("game.csv", (string?)m["name"]);
        Assert.Equal(File.ReadAllText(path), (string?)m["csv"]);
        Assert.Equal(RemoteCrypto.Rev(File.ReadAllText(path)), (string?)m["rev"]);
    }

    [AvaloniaFact]
    public async Task Get_with_unsaved_edits_asks_the_patient_to_save_and_sends_nothing()
    {
        var w = NewWindow();
        w.OpenPath(SavedProfile());
        w.OpenFile!.Dirty = true;
        var r = await Connected(w);
        string? notice = null;
        r.Session.Notice += t => notice = t;
        await r.Say("profile.get", new JsonObject { ["id"] = "g1" });

        Assert.Equal("profile.error", (string?)r.Last["type"]);
        Assert.Equal("unsaved", (string?)r.Last["code"]);
        Assert.Null(r.Last["csv"]);
        Assert.Equal(Strings.Remote_SaveFirst, notice);
        w.OpenFile!.Dirty = false; // else Close waits on the save prompt
    }

    [AvaloniaFact]
    public async Task Get_with_an_untitled_profile_is_unsaved_too()
    {
        var w = NewWindow();
        var f = ProfileFile.Load(Profile);
        w.LoadProfile(f);
        var r = await Connected(w);
        await r.Say("profile.get", new JsonObject { ["id"] = "g1" });
        Assert.Equal("unsaved", (string?)r.Last["code"]);
    }

    [AvaloniaFact]
    public async Task Get_with_nothing_open_and_no_quadstick_is_none()
    {
        var r = await Connected();
        await r.Say("profile.get", new JsonObject { ["id"] = "g1" });
        Assert.Equal("profile.error", (string?)r.Last["type"]);
        Assert.Equal("none", (string?)r.Last["code"]);
    }

    [AvaloniaFact]
    public async Task Get_with_nothing_open_offers_the_devices_default_csv()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_dir, "QUADSTICK")).FullName;
        File.WriteAllText(Path.Combine(drive, "default.csv"), Profile);
        var r = await Connected(NewWindow(() => new[] { drive }));
        await r.Say("profile.get", new JsonObject { ["id"] = "g1" });
        Assert.Equal("profile", (string?)r.Last["type"]);
        Assert.Equal("default.csv", (string?)r.Last["name"]);
        Assert.Equal(Profile, (string?)r.Last["csv"]);
    }

    [AvaloniaFact]
    public async Task A_profile_too_big_for_one_frame_is_refused_not_sent_broken()
    {
        var big = Profile + string.Concat(Enumerable.Repeat("kb_a,normal,soft_sip,,,,,,,a long comment to fill the frame\n", 900));
        var w = NewWindow();
        w.OpenPath(SavedProfile(big));
        var r = await Connected(w);
        await r.Say("profile.get", new JsonObject { ["id"] = "g1" });
        Assert.Equal("profile.error", (string?)r.Last["type"]);
        Assert.Equal("too_big", (string?)r.Last["code"]);
        Assert.All(r.Socket.Sent, f => Assert.True(f.Length <= RemoteSession.MaxFrame));
    }

    // ---- profile.propose ----

    async Task<(Rig Rig, string Path)> WithSavedProfile(Func<RemoteProposal, Task<bool>> ask, bool installs = false)
    {
        var w = NewWindow();
        var path = SavedProfile();
        w.OpenPath(path);
        w.AskRemoteProposalOverride = ask;
        w.OfferRemoteInstallOverride = () => Task.FromResult(installs);
        return (await Connected(w), path);
    }

    static JsonObject Propose(string csv, string baseRev, string id = "p1") =>
        new() { ["id"] = id, ["name"] = "game.csv", ["csv"] = csv, ["baseRev"] = baseRev, ["note"] = "Softer sip" };

    [AvaloniaFact]
    public async Task A_proposal_on_an_old_rev_is_stale_and_shows_nothing()
    {
        bool asked = false;
        var (r, path) = await WithSavedProfile(_ => { asked = true; return Task.FromResult(true); });
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev("something else")));
        await r.Session.PendingProposal!;

        Assert.False(asked);
        Assert.Equal("profile.result", (string?)r.Last["type"]);
        Assert.Equal("p1", (string?)r.Last["re"]);
        Assert.False((bool)r.Last["ok"]!);
        Assert.Equal("stale", (string?)r.Last["code"]);
        Assert.Equal(Profile, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_proposal_over_unsaved_edits_is_stale()
    {
        var (r, path) = await WithSavedProfile(_ => Task.FromResult(true));
        r.Window.OpenFile!.Dirty = true;
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;
        Assert.Equal("stale", (string?)r.Last["code"]);
        Assert.Equal(Profile, File.ReadAllText(path));
        r.Window.OpenFile!.Dirty = false;
    }

    [AvaloniaFact]
    public async Task An_invalid_proposal_sends_the_validator_errors_and_shows_nothing()
    {
        bool asked = false;
        var (r, path) = await WithSavedProfile(_ => { asked = true; return Task.FromResult(true); });
        var bad = Changed.Replace("game.csv", "NUL.csv");
        await r.Say("profile.propose", Propose(bad, RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;

        Assert.False(asked);
        Assert.Equal("invalid", (string?)r.Last["code"]);
        Assert.NotEmpty(r.Last["errors"]!.AsArray());
        Assert.Equal(Profile, File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Text_that_is_not_a_profile_is_invalid()
    {
        var (r, _) = await WithSavedProfile(_ => Task.FromResult(true));
        await r.Say("profile.propose", Propose("hello there", RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;
        Assert.Equal("invalid", (string?)r.Last["code"]);
    }

    [AvaloniaFact]
    public async Task Keep_declines_and_writes_nothing()
    {
        RemoteProposal? seen = null;
        var (r, path) = await WithSavedProfile(p => { seen = p; return Task.FromResult(false); });
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;

        Assert.Equal("Softer sip", seen!.Note);
        Assert.Equal(Changed, seen.Csv);
        Assert.Equal("declined", (string?)r.Last["code"]);
        Assert.Equal(Profile, File.ReadAllText(path));
        Assert.False(Directory.Exists(CrashGuard.RescueDir) && Directory.GetFiles(CrashGuard.RescueDir).Length > 0);
    }

    [AvaloniaFact]
    public async Task Use_this_saves_keeps_a_rescue_copy_opens_it_and_offers_install()
    {
        var (r, path) = await WithSavedProfile(_ => Task.FromResult(true), installs: false);
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;

        Assert.True((bool)r.Last["ok"]!);
        Assert.Equal(RemoteCrypto.Rev(Changed), (string?)r.Last["rev"]);
        Assert.False((bool)r.Last["installed"]!);
        Assert.Equal(Changed, File.ReadAllText(path));
        var rescue = Assert.Single(Directory.GetFiles(CrashGuard.RescueDir, "game-before-clinician-*.csv"));
        Assert.Equal(Profile, File.ReadAllText(rescue));
        Assert.Equal(path, r.Window.CurrentProfilePath);
        Assert.Contains(r.Window.OpenFile!.Document.Sheets[0].Bindings, b => b.Output == "cross");

        // The next get serves what was just saved, so the clinician's rev agrees.
        await r.Say("profile.get", new JsonObject { ["id"] = "g2" });
        Assert.Equal(RemoteCrypto.Rev(Changed), (string?)r.Last["rev"]);
    }

    [AvaloniaFact]
    public async Task An_install_that_finished_is_reported()
    {
        var (r, _) = await WithSavedProfile(_ => Task.FromResult(true), installs: true);
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;
        Assert.True((bool)r.Last["installed"]!);
    }

    [AvaloniaFact]
    public async Task A_second_proposal_while_the_dialog_is_open_is_busy()
    {
        var answer = new TaskCompletionSource<bool>();
        var (r, path) = await WithSavedProfile(_ => answer.Task);
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile), "p1"));
        var first = r.Session.PendingProposal!;
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile), "p2"));
        await r.Session.PendingProposal!;

        Assert.Equal("p2", (string?)r.Last["re"]);
        Assert.Equal("busy", (string?)r.Last["code"]);
        Assert.Equal(Profile, File.ReadAllText(path));

        answer.SetResult(false);
        await first;
        Assert.Equal("p1", (string?)r.Last["re"]);
        Assert.Equal("declined", (string?)r.Last["code"]);
    }

    [AvaloniaFact]
    public async Task A_file_changed_while_the_dialog_was_open_is_stale_and_not_overwritten()
    {
        var answer = new TaskCompletionSource<bool>();
        var (r, path) = await WithSavedProfile(_ => answer.Task);
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        var pending = r.Session.PendingProposal!;
        File.WriteAllText(path, Profile + "kb_b,normal,soft_puff\n"); // saved from elsewhere meanwhile
        answer.SetResult(true);
        await pending;

        Assert.Equal("stale", (string?)r.Last["code"]);
        Assert.Contains("kb_b", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task A_proposal_for_the_devices_own_file_is_saved_to_the_library_not_the_drive()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_dir, "QUADSTICK")).FullName;
        File.WriteAllText(Path.Combine(drive, "default.csv"), Profile);
        var w = NewWindow(() => new[] { drive });
        w.AskRemoteProposalOverride = _ => Task.FromResult(true);
        w.OfferRemoteInstallOverride = () => Task.FromResult(false);
        var r = await Connected(w);
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        await r.Session.PendingProposal!;

        Assert.True((bool)r.Last["ok"]!);
        Assert.Equal(Profile, File.ReadAllText(Path.Combine(drive, "default.csv")));
        Assert.Equal(Changed, File.ReadAllText(Path.Combine(MainWindow.LibraryDir, "default.csv")));
    }

    [AvaloniaFact]
    public async Task A_dialog_still_open_when_the_session_drops_cannot_reply()
    {
        var answer = new TaskCompletionSource<bool>();
        var (r, _) = await WithSavedProfile(_ => answer.Task);
        await r.Say("profile.propose", Propose(Changed, RemoteCrypto.Rev(Profile)));
        var pending = r.Session.PendingProposal!;
        await r.Session.HandleFrameAsync("{\"relay\":\"peer-left\"}");
        int n = r.Socket.Sent.Count;
        answer.SetResult(false);
        await pending;
        Assert.Equal(n, r.Socket.Sent.Count);
    }

    [Fact]
    public void The_change_list_names_the_mode_and_the_rows()
    {
        var lines = MainWindow.RemoteChanges(Profile, Changed);
        Assert.Contains(lines, l => l.Contains("Solo"));
        Assert.Contains(lines, l => l.Contains("circle") && l.StartsWith(Strings.Remote_RowRemoved.Split('{')[0]));
        Assert.Contains(lines, l => l.Contains("cross") && l.StartsWith(Strings.Remote_RowAdded.Split('{')[0]));
        Assert.DoesNotContain(lines, l => l.Contains("square"));
        Assert.Equal(new[] { Strings.Remote_NoChanges }, MainWindow.RemoteChanges(Profile, Profile));
        Assert.Equal(new[] { Strings.Remote_OtherChanges },
            MainWindow.RemoteChanges(Profile, Profile + "\n"));
    }

    [Fact]
    public void A_change_outside_the_bindings_is_listed_beside_one_inside_them()
    {
        // The binding change is easy to see; the renamed header and the comment
        // added past the inputs are the ones a summary could hide.
        var sneaky = Changed.Replace("game.csv", "other.csv").Replace("square,normal,lip", "square,normal,lip,,,,,,,,note");
        var lines = MainWindow.RemoteChanges(Profile, sneaky);
        Assert.Contains(lines, l => l.Contains("cross"));
        Assert.Contains(Strings.Remote_OtherLines, lines);
        Assert.Contains(lines, l => l.Contains("other.csv") && l.StartsWith(Strings.Remote_RowAdded.Split('{')[0]));
        Assert.Contains(lines, l => l.Contains("note") && l.StartsWith(Strings.Remote_RowAdded.Split('{')[0]));
    }

    [Fact]
    public void Rows_that_only_moved_say_so()
    {
        var moved = "Profile Name,,Solo\ngame.csv\nOutputs,Function,usb\ncircle,normal,right_sip\nsquare,normal,lip\n";
        Assert.Equal(new[] { Strings.Remote_OtherChanges }, MainWindow.RemoteChanges(Profile, moved));
    }

    // ---- live ----

    static LiveState Sending(double x, params string[] outputs) =>
        new(x, 0, new[] { 1 }, "QuadStick", new HashSet<string>(outputs, StringComparer.Ordinal), true);

    [AvaloniaFact]
    public async Task Live_frames_go_only_on_change_and_at_most_ten_a_second()
    {
        var w = NewWindow();
        w.OpenPath(SavedProfile());
        var r = await Connected(w);
        long now = 1000;
        LiveState? state = Sending(0, "square");
        r.Session.NowMs = () => now;
        r.Session.LiveSource = () => state;

        await r.Say("live.start");
        Assert.True(r.Session.LiveOn);
        int start = r.Socket.Sent.Count;

        r.Session.PumpLive();
        var first = r.Last;
        Assert.Equal("live", (string?)first["type"]);
        var lit = Assert.Single(first["lit"]!.AsArray());
        Assert.Equal("square", (string?)lit!["output"]);
        Assert.Equal("lip", (string?)lit["input"]);
        Assert.Equal(new long[] { 1 }, first["buttons"]!.AsArray().Select(b => (long)b!));
        Assert.Equal(2, first["axes"]!.AsArray().Count);

        now += 50; r.Session.PumpLive();            // nothing changed
        Assert.Equal(start + 1, r.Socket.Sent.Count);

        state = Sending(0.5, "circle");
        now += 10; r.Session.PumpLive();            // changed, but inside 100 ms
        state = Sending(0.7, "circle");
        now += 10; r.Session.PumpLive();
        Assert.Equal(start + 1, r.Socket.Sent.Count);

        now = 1100; r.Session.PumpLive();           // the window is over: the latest state goes
        Assert.Equal(start + 2, r.Socket.Sent.Count);
        Assert.Equal(0.7, (double)r.Last["axes"]![0]!);
        Assert.Equal("circle", (string?)r.Last["lit"]![0]!["output"]);
        Assert.Equal(100, (long)r.Last["t"]!);

        state = null; now += 500; r.Session.PumpLive(); // no QuadStick: nothing sent
        Assert.Equal(start + 2, r.Socket.Sent.Count);

        await r.Say("live.stop");
        Assert.False(r.Session.LiveOn);
        state = Sending(0, "square"); now += 500; r.Session.PumpLive();
        Assert.Equal(start + 2, r.Socket.Sent.Count);
    }

    [AvaloniaFact]
    public async Task Live_with_no_profile_open_lights_nothing_but_still_sends_the_stick()
    {
        var r = await Connected();
        long now = 0;
        r.Session.NowMs = () => now;
        r.Session.LiveSource = () => Sending(0.25, "square");
        await r.Say("live.start");
        r.Session.PumpLive();
        Assert.Equal("live", (string?)r.Last["type"]);
        Assert.Empty(r.Last["lit"]!.AsArray());
    }

    // ---- the window ----

    [AvaloniaFact]
    public async Task The_window_shows_the_code_and_says_where_the_session_stands()
    {
        var w = NewWindow();
        var socket = new FakeSocket();
        var win = new RemoteSessionWindow(w, new Uri("wss://relay.test")) { NewSocket = _ => socket };
        win.Show(w);
        Ui.Click(win.StartButton);

        var s = win.Session!;
        Assert.Equal(Strings.Remote_Waiting, win.StatusText);
        var code = FindText(win, RemoteCrypto.Display(s.Code));
        Assert.Contains(string.Join(" ", s.Code.ToCharArray()), AutomationProperties.GetName(code));
        Assert.False(win.StartButton.IsVisible);

        var clinician = new Clinician(s.Code);
        await s.HandleFrameAsync(clinician.Frame("hello", new JsonObject { ["role"] = "clinician" }));
        Assert.Equal(Strings.Remote_Connected, win.StatusText);
        Assert.False(win.WatchingShown);
        await s.HandleFrameAsync(clinician.Frame("live.start"));
        Assert.True(win.WatchingShown);
        Assert.NotNull(FindText(win, Strings.Remote_Watching));
        await s.HandleFrameAsync(clinician.Frame("live.stop"));
        Assert.False(win.WatchingShown);

        Ui.Click(win.EndButton);
        await Task.Yield();
        Assert.Equal(Strings.Remote_Ended, win.StatusText);
        Assert.True(win.StartButton.IsVisible);
        Assert.Equal(Strings.Remote_StartAgain, win.StartButton.Content);
        Assert.True(socket.Closed);
        win.Close();
    }

    [AvaloniaFact]
    public void Every_button_in_the_window_has_a_name_a_screen_reader_can_say()
    {
        var w = NewWindow();
        var win = new RemoteSessionWindow(w, new Uri("wss://relay.test")) { NewSocket = _ => new FakeSocket() };
        win.Show(w);
        Ui.Click(win.StartButton);
        foreach (var b in Descendants(win).OfType<Button>())
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b) is { Length: > 0 } n ? n : b.Content as string),
                "a button with no name");
        win.Close();
    }

    [AvaloniaFact]
    public void Closing_the_window_ends_the_session()
    {
        var w = NewWindow();
        var socket = new FakeSocket();
        var win = new RemoteSessionWindow(w, new Uri("wss://relay.test")) { NewSocket = _ => socket };
        win.Show(w);
        Ui.Click(win.StartButton);
        win.Close();
        Assert.Equal(RemoteStatus.Ended, win.Session!.Status);
        Assert.True(socket.Closed);
    }

    [AvaloniaFact]
    public void The_editors_share_menu_offers_a_clinician_session()
    {
        var w = NewWindow();
        w.OpenPath(SavedProfile());
        var share = w.FindControl<Button>("ShareButton")!;
        var menu = Assert.IsType<MenuFlyout>(share.Flyout);
        var item = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == Strings.Remote_ShareWithClinician);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.NotNull(w.RemoteWindowForTest);
        w.RemoteWindowForTest!.Close();
    }

    [AvaloniaFact]
    public void Home_starts_a_session_that_serves_the_plugged_in_quadstick()
    {
        var drive = Directory.CreateDirectory(Path.Combine(_dir, "QUADSTICK")).FullName;
        File.WriteAllText(Path.Combine(drive, "default.csv"), Profile);
        var w = NewWindow(() => new[] { drive });
        var home = w.FindControl<Button>("HomeRemoteButton")!;
        Assert.True(home.IsVisible);
        Assert.Equal(Strings.Remote_ShareFromHomeName, AutomationProperties.GetName(home));
        Ui.Click(home);
        Assert.NotNull(w.RemoteWindowForTest);
        var (served, error) = w.RemoteCurrentProfile();
        Assert.Null(error);
        Assert.Equal(Path.Combine(drive, "default.csv"), served!.Path);
        Assert.True(served.FromDevice);
        w.RemoteWindowForTest!.Close();
    }

    static IEnumerable<Avalonia.Visual> Descendants(Avalonia.Visual v) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v);

    static TextBlock FindText(Window win, string text) =>
        Descendants(win).OfType<TextBlock>().First(t => t.Text == text);
}
