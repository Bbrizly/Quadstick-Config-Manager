using System.Diagnostics;
using System.Text.Json;

namespace QuadStick.App;

/// <summary>One event from the agent. Everything the window draws comes from
/// these and nothing else, so what the run says and what the window shows
/// cannot drift apart.</summary>
public sealed class AgentEvent
{
    public string Kind { get; init; } = "";
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Text { get; init; } = "";
    public string State { get; init; } = "";
    /// <summary>The raw JSON of this event, shown when a card is expanded. It is
    /// what the agent actually said, not a retelling of it.</summary>
    public string Raw { get; init; } = "";
    public JsonElement Body { get; init; }

    public JsonElement? Get(string name) =>
        Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty(name, out var v) ? v : null;

    public string Str(string name) => Get(name)?.ValueKind == JsonValueKind.String
        ? Get(name)!.Value.GetString() ?? "" : "";

    public int Num(string name) => Get(name) is { ValueKind: JsonValueKind.Number } n
        ? n.GetInt32() : 0;

    public IReadOnlyList<JsonElement> List(string name) =>
        Get(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList()
                                                         : Array.Empty<JsonElement>();
}

/// <summary>A run in progress. The window talks to this and never to a process,
/// so a test can drive the whole window from a scripted stream with no model,
/// no network and no python involved.</summary>
public interface IAgentRun : IDisposable
{
    event Action<AgentEvent>? Event;
    event Action<string>? Trouble;
    event Action<int>? Ended;
    void Start();
    void Reply(object answer);
}

/// <summary>Runs the agent as one process and turns its output into events.
///
/// The app never writes a profile cell itself. It starts this, shows what comes
/// back, and sends an answer only when a person clicks one. Every write still
/// happens on the agent's side, through the same refusals the terminal path
/// goes through, so there is one write path and not two.</summary>
public sealed class AgentBridge : IAgentRun
{
    readonly Process _process;
    bool _disposed;

    public event Action<AgentEvent>? Event;
    public event Action<string>? Trouble;
    public event Action<int>? Ended;

    /// <summary>Where agent/ lives. Found from the app rather than fixed, so a
    /// build running out of the repo and one running from Applications both
    /// look in a place that can actually exist.</summary>
    public static string? FindAgentRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "agent", "run.py")))
                return dir.FullName;
        return null;
    }

    public AgentBridge(string root, IEnumerable<string> arguments, string? python = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = python ?? "python3",
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-u");                       // unbuffered, or the cards arrive in a lump
        start.ArgumentList.Add(Path.Combine(root, "agent", "run.py"));
        foreach (var a in arguments) start.ArgumentList.Add(a);
        // The agent shells out to qsf, which is a dotnet build. Without this it
        // finds no runtime and every step fails with the same opaque message.
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Read(e.Data); };
        // stderr is not the protocol, but a traceback there is the only clue
        // when the protocol stops arriving, so it is kept rather than dropped.
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Trouble?.Invoke(e.Data); };
    }

    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // Ended is raised only after the readers have drained, which is what
        // the parameterless WaitForExit waits for. The Exited event fires as
        // soon as the process is gone, so it could report the run over before
        // the last `done` line had been read, and the window would say nothing
        // was written a moment before showing what was.
        _ = Task.Run(() =>
        {
            try { _process.WaitForExit(); }
            catch (SystemException) { }
            Ended?.Invoke(Code());
        });
    }

    int Code()
    {
        try { return _process.ExitCode; }
        catch (SystemException) { return -1; }
    }

    void Read(string line)
    {
        JsonElement body;
        try { body = JsonDocument.Parse(line).RootElement.Clone(); }
        // A line that is not an event is not thrown away silently: it is the
        // agent trying to say something, and dropping it is how a window ends
        // up blank with no reason on screen.
        catch (JsonException) { Trouble?.Invoke(line); return; }
        if (body.ValueKind != JsonValueKind.Object) { Trouble?.Invoke(line); return; }

        string Text(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
        Event?.Invoke(new AgentEvent
        {
            Kind = Text("event"), Id = Text("id"), Title = Text("title"),
            Subtitle = Text("subtitle"), Text = Text("text"), State = Text("state"),
            Raw = line, Body = body,
        });
    }

    /// <summary>Answer a question, or approve a write. Nothing else is ever
    /// sent, so the agent cannot be steered from here by anything but a click.</summary>
    public void Reply(object answer)
    {
        if (_disposed) return;
        try
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(answer));
            _process.StandardInput.Flush();
        }
        // The process can exit between the check and the write, so the check is
        // not the guard, this is. An answer that never arrived must say so:
        // silence here leaves a person looking at a choice they think they made.
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                     or InvalidOperationException)
        {
            Trouble?.Invoke($"That answer did not reach the agent ({ex.Message}). "
                          + "It had already stopped, so nothing was written.");
        }
    }

    /// <summary>Closing the pipe is how the agent is told the window is gone.
    /// It stops at its next question rather than writing anything.
    ///
    /// The waiting happens off the UI thread. Doing it inline froze the whole
    /// app for two seconds whenever the agent was busy in a model call rather
    /// than sitting at a question.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.StandardInput.Close(); }
        catch (SystemException) { }
        _ = Task.Run(() =>
        {
            try
            {
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
            catch (SystemException) { }
            _process.Dispose();
        });
    }
}
