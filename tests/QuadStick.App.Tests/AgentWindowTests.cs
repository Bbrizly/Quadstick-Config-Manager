using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// The window that shows the agent working. Everything here is driven from a
// scripted event stream, so no model, no network and no python are involved.
//
// What is pinned: nothing is written until a person presses a button, an answer
// goes back exactly as it was shown, a run that stops says so instead of going
// quiet, and every card can be read aloud.
public sealed class AgentWindowTests
{
    /// <summary>A run whose events a test hands over one at a time, and which
    /// records every reply the window sends back.</summary>
    sealed class Scripted : IAgentRun
    {
        public event Action<AgentEvent>? Event;
        public event Action<string>? Trouble;
        public event Action<int>? Ended;

        public List<string> Replies { get; } = new();
        public bool Disposed { get; private set; }

        public void Start() { }

        public void Say(string json)
        {
            var body = JsonDocument.Parse(json).RootElement.Clone();
            string Text(string name) => body.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            Event?.Invoke(new AgentEvent
            {
                Kind = Text("event"), Id = Text("id"), Title = Text("title"),
                Subtitle = Text("subtitle"), Text = Text("text"), State = Text("state"),
                Raw = json, Body = body,
            });
            Dispatcher.UIThread.RunJobs();
            Watching?.UpdateLayout();
        }

        public void Complain(string line) { Trouble?.Invoke(line); Dispatcher.UIThread.RunJobs(); }
        public void End(int code) { Ended?.Invoke(code); Dispatcher.UIThread.RunJobs(); }

        /// <summary>The window this run is feeding, so a new card is laid out
        /// before a test goes looking for it in the visual tree.</summary>
        public Window? Watching { get; set; }
        public void Reply(object answer) => Replies.Add(JsonSerializer.Serialize(answer));
        public void Dispose() => Disposed = true;
    }

    static (MainWindow main, AgentWindow window, Scripted run) Open(string typed = "Elden Ring")
    {
        var main = new MainWindow();
        main.Show();
        var run = new Scripted();
        var window = new AgentWindow(main, root: "/nowhere") { StartWith = _ => run };
        window.Show();
        run.Watching = window;
        Type(window, typed);
        Press(window, "Set it up");
        window.UpdateLayout();
        return (main, window, run);
    }

    static void Type(Window window, string text)
    {
        var box = window.GetVisualDescendants().OfType<TextBox>().First();
        box.Text = text;
        Dispatcher.UIThread.RunJobs();
    }

    static Button Find(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>()
              .First(b => Label(b).Contains(content, StringComparison.OrdinalIgnoreCase));

    static string Label(Button b) => b.Content as string
        ?? string.Join(" ", (b.Content as Control)?.GetVisualDescendants().OfType<TextBlock>()
                                .Select(t => t.Text) ?? Array.Empty<string?>());

    static void Press(Window window, string content)
    {
        Find(window, content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    static string Words(Window window) => string.Join("\n",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

    static readonly string Question = """
        {"event":"question","id":"q1","output":"kb_left_shift",
         "question":"Sprint: hold it, or press once to keep running?",
         "options":[{"inputs":["mp_triple_puff"],"function":"delay_on","label":"Triple puff, held, as in Apex"},
                    {"inputs":["lip"],"function":"toggle","label":"Lip, press once"},
                    {"inputs":[],"function":"normal","label":"Leave it unbound"}]}
        """.ReplaceLineEndings(" ");

    static readonly string Confirm = """
        {"event":"confirm","id":"c1","profile":"/tmp/elden-ring.csv",
         "rows":[{"output":"kb_left_shift","inputs":["mp_triple_puff"],"function":"delay_on",
                  "why":"they answered the question about this control"}],
         "open":[{"output":"kb_c","question":"crouch?"}],"untouched":["kb_v"]}
        """.ReplaceLineEndings(" ");

    // A tool call shows what it is doing while it runs, and what came back when
    // it is done. The word beside it changes; the card does not vanish.
    [AvaloniaFact]
    public void AToolCallShowsWhatItDidAndHowItWent()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Read how Elden Ring is controlled","subtitle":"searching the web","state":"running"}""");

        var card = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().Single();
        Assert.Contains("working", card.StateWord);
        Assert.Contains("Read how Elden Ring is controlled", Words(window));

        run.Say("""{"event":"tool_done","id":"t1","state":"ok","summary":"24 controls the device knows, 0 dropped"}""");
        Assert.Contains("done", card.StateWord);
        Assert.Contains("24 controls the device knows", Words(window));
        // Still one card. A result replaces the state of the call it belongs to
        // rather than piling a second card on top of it.
        Assert.Single(window.GetVisualDescendants().OfType<AgentWindow.ToolCard>());
    }

    // Colour is never the only signal, and every card reads aloud.
    [AvaloniaFact]
    public void EveryCardSaysItsStateInWords()
    {
        var (_, window, run) = Open();
        foreach (var state in new[] { "running", "ok", "warn", "failed" })
        {
            run.Say($$"""{"event":"tool","id":"t{{state}}","title":"A step","state":"{{state}}"}""");
        }
        var cards = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().ToList();
        Assert.Equal(4, cards.Count);
        foreach (var card in cards)
        {
            // A glyph plus a word, never a glyph alone.
            Assert.True(card.StateWord.Split(' ').Length >= 2, card.StateWord);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(card)));
        }
    }

    // The answer that goes back is the one they pressed, by index, and nothing
    // else is ever sent.
    [AvaloniaFact]
    public void AnAnswerGoesBackExactlyAsItWasShown()
    {
        var (_, window, run) = Open();
        run.Say(Question);
        Assert.Contains("Sprint: hold it", Words(window));

        Press(window, "Lip, press once");
        Assert.Equal(new[] { """{"id":"q1","choice":1}""" }, run.Replies);
        // What they chose stays on screen, so they can check later what they
        // agreed to rather than having to remember it.
        Assert.Contains("You chose: lip, toggle", Words(window));
    }

    [AvaloniaFact]
    public void LeavingAControlAloneSendsNoChoice()
    {
        var (_, window, run) = Open();
        run.Say(Question);
        Press(window, "Leave this one alone");
        Assert.Equal(new[] { """{"id":"q1","choice":null}""" }, run.Replies);
    }

    // An option with nothing to trigger it is the offer to leave the control
    // alone, and it says so rather than looking like a binding.
    [AvaloniaFact]
    public void AnOptionThatBindsNothingSaysSo()
    {
        var (_, window, run) = Open();
        run.Say(Question);
        var leave = Find(window, "Leave it unbound");
        Assert.Contains("leaves this control unbound", AutomationProperties.GetName(leave),
                        StringComparison.OrdinalIgnoreCase);
    }

    // Nothing is written until the button is pressed, and the card says what is
    // NOT being written as well as what is.
    [AvaloniaFact]
    public void TheWriteWaitsForAPersonAndNamesWhatIsLeftOut()
    {
        var (_, window, run) = Open();
        run.Say(Confirm);
        Assert.Empty(run.Replies);
        var words = Words(window);
        Assert.Contains("Write 1 binding?", words);
        Assert.Contains("Nothing has been written yet", words);
        // The two controls that stay unbound are named on the same card.
        Assert.Contains("kb_c", words);
        Assert.Contains("kb_v", words);

        Press(window, "Write it");
        Assert.Equal(new[] { """{"id":"c1","write":true}""" }, run.Replies);
    }

    [AvaloniaFact]
    public void DecliningTheWriteSaysSoAndSendsNo()
    {
        var (_, window, run) = Open();
        run.Say(Confirm);
        Press(window, "Do not write anything");
        Assert.Equal(new[] { """{"id":"c1","write":false}""" }, run.Replies);
        Assert.Contains("Nothing was written", Words(window));
    }

    // A run that stops has to say so. A window that goes quiet is the one
    // failure this screen must never have.
    [AvaloniaFact]
    public void ARunThatStopsSaysWhy()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"failed","message":"nothing usable was found about how that game is controlled"}""");
        var words = Words(window);
        Assert.Contains("Stopped. Nothing was written.", words);
        Assert.Contains("nothing usable was found", words);
    }

    [AvaloniaFact]
    public void ARunThatDiesWithoutSpeakingStillSaysSomething()
    {
        var (_, window, run) = Open();
        run.End(1);
        Assert.Contains("stopped before it finished", Words(window));
    }

    // Output that is not an event is still the agent trying to speak, so it is
    // shown rather than dropped.
    [AvaloniaFact]
    public void OutputThatIsNotAnEventIsStillShown()
    {
        var (_, window, run) = Open();
        run.Complain("Traceback (most recent call last):");
        Assert.Contains("Traceback", Words(window));
    }

    // The profile it wrote is opened by the app's own open path, so it lands in
    // the editor with the validator and the install button, like any other file.
    [AvaloniaFact]
    public void TheWrittenProfileIsHandedToTheEditor()
    {
        var (_, window, run) = Open();
        string? opened = null;
        window.OpenWritten = path => opened = path;
        run.Say("""{"event":"done","profile":"/tmp/elden-ring.csv","written":19,"errors":0,"warnings":2,"issues":[],"open":[],"untouched":[]}""");
        Assert.Contains("19 bindings written to elden-ring.csv", Words(window));
        Assert.Contains("0 errors and 2 warnings", Words(window));

        Press(window, "Open it in the editor");
        Assert.Equal("/tmp/elden-ring.csv", opened);
    }

    // A run that wrote nothing offers nothing to open, and says that plainly.
    [AvaloniaFact]
    public void ARunThatWroteNothingOffersNothingToOpen()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"done","profile":"/tmp/x.csv","written":0,"errors":0,"warnings":0,"issues":[],"open":[],"untouched":[]}""");
        Assert.Contains("Nothing was changed", Words(window));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                              b => Label(b).Contains("Open it in the editor"));
    }

    // Naming a game builds one. A sentence about the profile already open is a
    // change to that profile, and only when one is actually open.
    [AvaloniaFact]
    public void WhatTheyTypeDecidesWhetherThisBuildsOrEdits()
    {
        var main = new MainWindow();
        var window = new AgentWindow(main, root: "/nowhere");

        Assert.Equal(new[] { "--game", "Hollow Knight Silksong" },
                     window.Arguments("Hollow Knight Silksong", null, false));
        // No profile open, so even a verb builds a game rather than silently
        // editing something that is not there.
        Assert.Equal(new[] { "--game", "make sprint a hard puff" },
                     window.Arguments("make sprint a hard puff", null, false));
        Assert.Equal(new[] { "--edit", "/tmp/mine.csv", "--request", "make sprint a hard puff" },
                     window.Arguments("make sprint a hard puff", "/tmp/mine.csv", false));
        Assert.Contains("--replay", window.Arguments("Elden Ring", null, true));
    }

    // Asking for a change needs a file on disk to change. Saying so beats
    // quietly building a whole new profile because there was no path.
    [AvaloniaFact]
    public void AskingForAChangeWithNothingSavedSaysSo()
    {
        var main = new MainWindow();
        main.Show();
        var run = new Scripted();
        var window = new AgentWindow(main, root: "/nowhere", changing: true) { StartWith = _ => run };
        window.Show();
        run.Watching = window;
        Assert.Null(main.CurrentProfilePath);
        Assert.Contains("Save this profile first", Words(window));

        Type(window, "make sprint a hard puff");
        Press(window, "Work it out");
        // Nothing started, so nothing could have been written.
        Assert.Contains("Save this profile first", Words(window));
    }

    // In change mode every sentence is a change, verb or not. The guessing
    // only exists for the one window that could mean either.
    [AvaloniaFact]
    public void InChangeModeEverySentenceIsAChange()
    {
        var main = new MainWindow();
        var window = new AgentWindow(main, root: "/nowhere", changing: true);
        Assert.Equal(new[] { "--edit", "/tmp/mine.csv", "--request", "sprint should be lighter" },
                     window.Arguments("sprint should be lighter", "/tmp/mine.csv", false, changing: true));
    }

    // Closing the window closes the agent's pipe, which stops it at its next
    // question rather than leaving it writing for nobody.
    [AvaloniaFact]
    public void ClosingTheWindowStopsTheRun()
    {
        var (_, window, run) = Open();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(run.Disposed);
    }

    // Every binding on screen carries its reason, because a row nobody can
    // check is a row nobody should approve.
    [AvaloniaFact]
    public void EveryBindingShownCarriesItsReason()
    {
        var (_, window, run) = Open();
        run.Say("""
            {"event":"rows","title":"Settled from his own profiles",
             "rows":[{"output":"kb_space","inputs":["lip"],"function":"normal",
                      "why":"73 of 120 of his profiles do this (61%); nearest example A Plague Tale, row 28"}]}
            """.ReplaceLineEndings(" "));
        var words = Words(window);
        Assert.Contains("kb_space   lip, normal", words);
        Assert.Contains("73 of 120 of his profiles", words);
    }
}
