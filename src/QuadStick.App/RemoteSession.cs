using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

/// <summary>The one seam a test needs: text frames in and out. The real one is
/// <see cref="WebSocketTransport"/>; tests hand in a fake.</summary>
public interface IRemoteSocket : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
    /// <summary>The next text frame, or null once the socket has closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
    /// <summary>The close code the relay sent, once closed.</summary>
    int? CloseCode { get; }
    Task CloseAsync();
}

/// <summary>Why a connect failed, so the window can say the right sentence.</summary>
public sealed class RemoteConnectException(bool busy) : Exception
{
    public bool Busy { get; } = busy;
}

/// <summary>ClientWebSocket, with the 64 KB frame cap and a 503 told apart
/// from every other failure.</summary>
public sealed class WebSocketTransport : IRemoteSocket
{
    readonly ClientWebSocket _ws = new();
    readonly SemaphoreSlim _sending = new(1, 1);

    public int? CloseCode => _ws.CloseStatus is { } s ? (int)s : null;

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        _ws.Options.CollectHttpResponseDetails = true;
        try { await _ws.ConnectAsync(uri, ct); }
        catch (WebSocketException) when (_ws.HttpStatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        { throw new RemoteConnectException(busy: true); }
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        // ClientWebSocket allows one send at a time; live frames and replies
        // can both be on their way.
        await _sending.WaitAsync(ct);
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
        finally { _sending.Release(); }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[RemoteSession.MaxFrame];
        int used = 0;
        bool tooBig = false;
        while (true)
        {
            var chunk = used < buffer.Length ? buffer.AsMemory(used) : new byte[4096];
            var r = await _ws.ReceiveAsync(chunk, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            if (used < buffer.Length) used += r.Count; else tooBig = true;
            if (used >= buffer.Length && !r.EndOfMessage) tooBig = true;
            if (!r.EndOfMessage) continue;
            // An oversized frame is not something the relay would pass on. It
            // reads as a frame that does not decrypt, which is counted.
            if (tooBig || r.MessageType != WebSocketMessageType.Text) return "";
            return Encoding.UTF8.GetString(buffer, 0, used);
        }
    }

    public async Task CloseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        }
        catch { /* closing a dead socket has nothing left to say */ }
    }

    public void Dispose() { _ws.Dispose(); _sending.Dispose(); }
}

public enum RemoteStatus { Connecting, Waiting, Connected, Ended }

/// <summary>The patient side of a clinician session: one code, one socket, one
/// clinician. Everything here runs on the UI thread (the receive loop is
/// awaited from it), so it never races the editor it reads from.</summary>
/// <remarks>
/// The clinician can ask and can propose. Nothing they send writes a file or
/// touches the device: a proposal opens a dialog, and only the patient's click
/// saves it. A dropped socket ends the session for good. There is no resume and
/// nothing is resent.
/// </remarks>
public sealed class RemoteSession
{
    public const string DefaultRelay = "wss://relay.bassamkamal.dev";
    public const int MaxFrame = 64 * 1024;
    internal const int LiveIntervalMs = 100; // at most 10 live frames a second
    const int MaxBadFrames = 3;

    readonly MainWindow _owner;
    readonly IRemoteSocket _socket;
    byte[] _key = [];
    readonly Task _derived;
    readonly CancellationTokenSource _stop = new();
    DispatcherTimer? _liveTimer;

    long _sentSeq;
    long _peerSeq;
    int _badFrames;
    bool _proposalOpen;
    bool _helloSent;
    bool _liveOn;
    string? _lastLive;
    long _lastLiveAt = long.MinValue / 2;
    long _liveStartedAt;

    public string Code { get; }
    /// <summary>Empty until RunAsync has derived it.</summary>
    public string Room { get; private set; } = "";
    public RemoteStatus Status { get; private set; } = RemoteStatus.Connecting;
    /// <summary>Why the session ended, in words for the patient. Null while it runs.</summary>
    public string? EndReason { get; private set; }
    public bool LiveOn => _liveOn;

    public event Action? Changed;
    /// <summary>Something the patient should read now, such as "save first".</summary>
    public event Action<string>? Notice;

    /// <summary>Test seams. The clock decides live coalescing; the reading and
    /// the sheet are what the editor already lights rows from.</summary>
    internal Func<long> NowMs = () => Environment.TickCount64;
    internal TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    internal Func<LiveState?> LiveSource;
    internal Func<ModeSheet?> LiveSheet;
    /// <summary>The proposal the dialog is answering, for a test to await.</summary>
    internal Task? PendingProposal { get; private set; }

    public RemoteSession(MainWindow owner, IRemoteSocket socket, string code)
    {
        _owner = owner;
        _socket = socket;
        Code = code;
        // 600000 PBKDF2 rounds: off the UI thread, while the window says Connecting.
        _derived = DeriveAsync(code);
        LiveSource = () => owner.LiveReading;
        LiveSheet = () => owner.RemoteLiveSheet;
    }

    /// <summary>The relay to use: QCM_RELAY_URL first (tests run against
    /// wrangler dev), then the setting, then the default. Plain ws:// is only
    /// allowed to this machine.</summary>
    public static Uri RelayBase(string? setting)
    {
        var raw = Environment.GetEnvironmentVariable("QCM_RELAY_URL") is { Length: > 0 } env ? env
            : setting is { Length: > 0 } s ? s : DefaultRelay;
        if (Uri.TryCreate(raw.TrimEnd('/'), UriKind.Absolute, out var u)
            && (u.Scheme == "wss" || (u.Scheme == "ws" && u.IsLoopback)))
            return u;
        return new Uri(DefaultRelay);
    }

    async Task DeriveAsync(string code) => (Room, _key) = await Task.Run(() => RemoteCrypto.Derive(code));

    public static string JoinLink(string code) => "https://clinic.bassamkamal.dev/#join=" + code;

    /// <summary>Connect, say hello, and read frames until the session ends.</summary>
    public async Task RunAsync(Uri relayBase)
    {
        try
        {
            await _derived;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await _socket.ConnectAsync(new Uri($"{relayBase.AbsoluteUri.TrimEnd('/')}/r/{Room}"), timeout.Token);
            }
        }
        catch (RemoteConnectException ex) when (ex.Busy) { Finish(Strings.Remote_Busy); return; }
        catch (Exception) when (!_stop.IsCancellationRequested) { Finish(Strings.Remote_Unreachable); return; }
        catch (Exception) { Finish(Strings.Remote_Ended); return; }

        // No hello yet: the relay drops a frame sent before the clinician is
        // there, and says peer-joined to both sides once they are.
        SetStatus(RemoteStatus.Waiting);
        await ReceiveLoopAsync();
    }

    async Task ReceiveLoopAsync()
    {
        while (Status != RemoteStatus.Ended)
        {
            string? frame;
            try { frame = await _socket.ReceiveAsync(_stop.Token); }
            catch (Exception) { frame = null; }
            if (Status == RemoteStatus.Ended) return;
            if (frame is null) { Finish(CloseReason(_socket.CloseCode)); return; }
            await HandleFrameAsync(frame);
        }
    }

    static string CloseReason(int? code) => code switch
    {
        4001 => Strings.Remote_RoomFull,
        4002 => Strings.Remote_Expired,
        4003 => Strings.Remote_TimeUp,
        4008 or 1009 => Strings.Remote_TooMuch,
        1013 => Strings.Remote_Busy,
        _ => Strings.Remote_Ended,
    };

    /// <summary>One frame from the relay. The loop calls this; tests call it
    /// directly with frames they sealed themselves.</summary>
    internal async Task HandleFrameAsync(string frame)
    {
        await _derived;
        if (Status == RemoteStatus.Ended) return;

        // The relay's own two frames are the only plaintext ones.
        if (frame.StartsWith('{'))
        {
            var relay = TryParse(frame) is JsonObject o ? Str(o, "relay") : null;
            if (relay == "peer-joined") await SendHelloAsync();
            else if (relay == "peer-left") await EndAsync(Strings.Remote_Ended, sayBye: false);
            return;
        }

        var plain = frame.Length <= MaxFrame ? RemoteCrypto.Open(_key, Room, frame) : null;
        if (plain is null)
        {
            if (++_badFrames >= MaxBadFrames) await EndAsync(Strings.Remote_CodeMismatch, sayBye: false);
            return;
        }

        if (TryParse(plain) is not JsonObject msg || Str(msg, "from") != "clinician")
        {
            // Reflection guard: one key serves both directions, so a frame of
            // ours sent back by the relay would open. It counts as a bad one.
            if (++_badFrames >= MaxBadFrames) await EndAsync(Strings.Remote_CodeMismatch, sayBye: false);
            return;
        }
        if (Int(msg, "v") != 1 || Int(msg, "seq") is not long seq) return;
        if (seq <= _peerSeq) return; // a replay, or a frame from before the last one
        _peerSeq = seq;

        var type = Str(msg, "type");
        if (type == "hello")
        {
            // Our own hello bounced back is not a clinician.
            if (Str(msg, "role") != "clinician") return;
            await SendHelloAsync(); // in answer, if their hello came before our peer-joined
            if (Status != RemoteStatus.Connected) SetStatus(RemoteStatus.Connected);
            return;
        }
        if (type == "bye") { await EndAsync(Strings.Remote_Ended, sayBye: false); return; }
        // Nothing but hello counts until the clinician has said who they are.
        if (Status != RemoteStatus.Connected) return;

        switch (type)
        {
            case "profile.get": await ReplyProfileAsync(Str(msg, "id")); break;
            case "profile.propose": PendingProposal = ProposeAsync(msg); break;
            case "live.start": StartLive(); break;
            case "live.stop": StopLive(); break;
            // Unknown types are ignored, so a newer clinic can talk to this app.
        }
    }

    // ---- profile.get ----

    async Task ReplyProfileAsync(string? id)
    {
        var (profile, error) = _owner.RemoteCurrentProfile();
        if (profile is null)
        {
            if (error == "unsaved")
            {
                _owner.RemoteAskToSave();
                Notice?.Invoke(Strings.Remote_SaveFirst);
            }
            await SendAsync(new JsonObject { ["type"] = "profile.error", ["re"] = id, ["code"] = error });
            return;
        }
        var reply = new JsonObject
        {
            ["type"] = "profile", ["re"] = id, ["name"] = profile.Name, ["csv"] = profile.Csv, ["rev"] = profile.Rev,
        };
        // ponytail: a profile too big for one 64 KB frame is refused, not
        // split. No real profile is near it; chunk the reply if one ever is.
        if (!await SendAsync(reply))
            await SendAsync(new JsonObject { ["type"] = "profile.error", ["re"] = id, ["code"] = "too_big" });
    }

    // ---- profile.propose ----

    async Task ProposeAsync(JsonObject msg)
    {
        var id = Str(msg, "id");
        Task Result(JsonObject r) { r["type"] = "profile.result"; r["re"] = id; return SendAsync(r); }
        Task Fail(string code) => Result(new JsonObject { ["ok"] = false, ["code"] = code });

        if (_proposalOpen) { await Fail("busy"); return; }
        var csv = Str(msg, "csv");
        var baseRev = Str(msg, "baseRev");
        if (csv is null) { await Fail("invalid"); return; }

        var (current, _) = _owner.RemoteCurrentProfile();
        // Unsaved edits are stale too: the clinician's base is not what is on screen.
        if (current is null || current.Rev != baseRev) { await Fail("stale"); return; }

        var errors = RemoteProposalErrors(csv);
        if (errors.Count > 0)
        {
            await Result(new JsonObject
            {
                ["ok"] = false, ["code"] = "invalid",
                ["errors"] = new JsonArray(errors.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
            });
            return;
        }

        _proposalOpen = true;
        bool use;
        try { use = await _owner.AskRemoteProposal(new RemoteProposal(current, csv, Str(msg, "note") ?? "")); }
        finally { _proposalOpen = false; }
        // A dialog that outlived the session still does what the patient
        // clicked; SendAsync drops the reply, since nobody is there to read it.
        if (!use) { await Fail("declined"); return; }

        var saved = await _owner.SaveRemoteProposalAsync(current, csv);
        if (saved.Code is { } code) { await Fail(code); return; }
        await Result(new JsonObject { ["ok"] = true, ["rev"] = saved.Rev, ["installed"] = saved.Installed });
    }

    /// <summary>What the existing Validator says is wrong, in its own words.
    /// Empty means the file may be shown to the patient.</summary>
    internal static List<string> RemoteProposalErrors(string csv)
    {
        ProfileFile file;
        try { file = ProfileFile.Load(csv); }
        catch (Exception) { return new List<string> { Strings.Remote_NotAProfile }; }
        if (file.Document.Sheets.Count == 0) return new List<string> { Strings.Remote_NotAProfile };
        return file.Issues.Where(i => i.Severity == Severity.Error)
            .Select(i => i.Cell.Length > 0 ? $"{i.Cell}: {i.Message}" : i.Message).ToList();
    }

    // ---- live ----

    void StartLive()
    {
        if (_liveOn) return;
        _liveOn = true;
        _lastLive = null;
        _liveStartedAt = NowMs();
        _liveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(LiveIntervalMs / 2), DispatcherPriority.Background,
            (_, _) => PumpLive());
        _liveTimer.Start();
        Changed?.Invoke();
    }

    void StopLive()
    {
        _liveTimer?.Stop();
        _liveTimer = null;
        if (!_liveOn) return;
        _liveOn = false;
        Changed?.Invoke();
    }

    /// <summary>Send what the stick is doing if it changed, no more often than
    /// every 100 ms. A change inside the window waits for the next pump, which
    /// then sends the latest state, so the clinician always ends on the truth.</summary>
    internal void PumpLive()
    {
        if (!_liveOn || Status != RemoteStatus.Connected) return;
        if (LiveSource() is not { } state) return; // no QuadStick connected
        var body = LiveBody(state, LiveSheet());
        var key = body.ToJsonString();
        if (key == _lastLive) return;
        var now = NowMs();
        if (now - _lastLiveAt < LiveIntervalMs) return;
        _lastLive = key;
        _lastLiveAt = now;
        body["type"] = "live";
        body["t"] = now - _liveStartedAt;
        _ = SendAsync(body);
    }

    static JsonObject LiveBody(LiveState state, ModeSheet? sheet) => new()
    {
        ["lit"] = new JsonArray(MainWindow.FiringBindings(state, sheet)
            .Select(b => (JsonNode)new JsonObject { ["input"] = string.Join("+", b.Inputs), ["output"] = b.Output })
            .ToArray()),
        ["buttons"] = new JsonArray(state.Buttons.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
        // Two places is plenty for a picture of the stick, and keeps a resting
        // stick's noise from reading as a change.
        ["axes"] = new JsonArray(JsonValue.Create(Math.Round(state.X, 2)), JsonValue.Create(Math.Round(state.Y, 2))),
    };

    // ---- sending and ending ----

    Task SendHelloAsync()
    {
        if (_helloSent) return Task.CompletedTask;
        _helloSent = true;
        return SendAsync(new JsonObject
        {
        ["type"] = "hello", ["role"] = "patient", ["app"] = "QCM",
            ["version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
        });
    }

    /// <summary>Seal and send. False when the frame would pass the relay's cap
    /// (nothing is sent), or the socket would not take it.</summary>
    async Task<bool> SendAsync(JsonObject body)
    {
        if (Status == RemoteStatus.Ended) return false;
        var msg = new JsonObject { ["v"] = 1, ["seq"] = _sentSeq + 1, ["from"] = "patient" };
        foreach (var (k, v) in body.ToList()) { body.Remove(k); msg[k] = v; }
        var frame = RemoteCrypto.Seal(_key, Room, msg.ToJsonString());
        if (Encoding.UTF8.GetByteCount(frame) > MaxFrame) return false;
        _sentSeq++;
        try { await _socket.SendAsync(frame, _stop.Token); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>End button, window closed, or the app quitting.</summary>
    public Task EndAsync() => EndAsync(Strings.Remote_Ended, sayBye: true);

    async Task EndAsync(string reason, bool sayBye)
    {
        if (Status == RemoteStatus.Ended) return;
        if (sayBye && Status != RemoteStatus.Connecting) await SendAsync(new JsonObject { ["type"] = "bye" });
        Finish(reason);
        await _socket.CloseAsync();
    }

    void Finish(string reason)
    {
        if (Status == RemoteStatus.Ended) return;
        StopLive();
        EndReason = reason;
        SetStatus(RemoteStatus.Ended);
        _stop.Cancel();
    }

    void SetStatus(RemoteStatus s) { Status = s; Changed?.Invoke(); }

    static JsonNode? TryParse(string s)
    {
        try { return JsonNode.Parse(s); } catch (JsonException) { return null; }
    }

    static string? Str(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static long? Int(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
}

/// <summary>What the clinician sent, beside what it would replace.</summary>
public sealed record RemoteProposal(RemoteProfile Current, string Csv, string Note);

/// <summary>A profile as the session serves it: name, saved text, its rev, and
/// where it came from.</summary>
public sealed record RemoteProfile(string Name, string Csv, string Rev, string Path, bool FromDevice);
