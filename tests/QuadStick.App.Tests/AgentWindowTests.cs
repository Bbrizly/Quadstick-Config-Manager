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

    // The real event stream, as agent/run.py emits it, drawn end to end. Every
    // kind has to be recognised: an unknown one falls back to dumping its raw
    // JSON on screen, which is the window admitting it does not understand its
    // own agent.
    [AvaloniaFact]
    public void ARealTranscriptDrawsWithNoRawJsonLeftOnScreen()
    {
        var (_, window, run) = Open();
        foreach (var line in new[]
        {
            """{"event":"run","mode":"live","model":"claude-sonnet-5","backend":"cli","says":"asks the model every time, nothing replayed"}""",
            """{"event":"stage","key":"research","title":"How Celeste is controlled"}""",
            """{"event":"tool","id":"chart","title":"Nobody has charted Celeste, so reading how it is controlled","subtitle":"searching the web","state":"running","detail":{"game":"Celeste"}}""",
            """{"event":"tool","id":"w1","title":"Searching the web for \u201cCeleste default controls\u201d","subtitle":"reading what comes back","state":"running","origin":"live"}""",
            """{"event":"tool_done","id":"w1","state":"ok","summary":"10 results","origin":"live"}""",
            """{"event":"tool_done","id":"chart","state":"warn","ms":160000,"origin":"live","summary":"17 controls the device knows, 3 the sources disagree about, 0 dropped"}""",
            """{"event":"stage","key":"history","title":"What his own profiles already answer"}""",
            """{"event":"tool","id":"predict","title":"Matched this game against every profile he has built","state":"running"}""",
            """{"event":"tool_done","id":"predict","state":"ok","ms":600,"origin":"local","summary":"37 of 67 answered from his own profiles"}""",
            """{"event":"rows","title":"Settled from his own profiles","rows":[{"output":"kb_z","inputs":["mp_right_puff"],"function":"normal","why":"51 of 96 of his profiles do this"}]}""",
            """{"event":"note","text":"I will settle the movement keys first."}""",
            """{"event":"tool","id":"m1","title":"Working out the ones his profiles cannot settle","subtitle":"step 1","state":"running"}""",
            """{"event":"tool_done","id":"m1","state":"ok","ms":23000,"origin":"live","summary":"decided what to do"}""",
        })
            run.Say(line);

        var shown = Words(window);
        Assert.DoesNotContain("\"event\"", shown);
        Assert.DoesNotContain("could not be drawn", shown);
        Assert.Contains("How Celeste is controlled".ToUpperInvariant(), shown);
        Assert.Contains("Searching the web", shown);
        Assert.Contains("160.0s", shown);
        Assert.Contains("asked the model", shown);
        Assert.Contains("on this machine, no model", shown);
    }

    // A step that is still going counts its own seconds. Without this a model
    // call that takes three minutes is indistinguishable from a run that hung,
    // and the whole window looks like it is doing nothing.
    [AvaloniaFact]
    public void AStepThatIsStillWorkingCountsItsOwnSeconds()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Working out what to do next","state":"running"}""");

        var card = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().Single();
        Assert.True(card.Running);
        Assert.DoesNotContain("s", card.StateWord.Replace("working", ""));

        card.Tick(4.2);
        Assert.Contains("4.2s", card.StateWord);
        card.Tick(11.9);
        Assert.Contains("11.9s", card.StateWord);

        // The run's own measure wins over whatever the window watched, because
        // the run is the thing that did the work.
        run.Say("""{"event":"tool_done","id":"t1","state":"ok","summary":"decided what to do","ms":8400}""");
        Assert.False(card.Running);
        Assert.Contains("8.4s", card.StateWord);
        // And it stops moving once it is settled.
        card.Tick(99);
        Assert.Contains("8.4s", card.StateWord);
    }

    // The step happening now is marked out from the ones that are done, so the
    // eye lands on it. The word still carries the state; this is a second signal.
    [AvaloniaFact]
    public void TheStepHappeningNowIsMarkedOutFromTheFinishedOnes()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Reading the control page","state":"running"}""");
        var card = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().Single();
        var working = card.BorderThickness;

        run.Say("""{"event":"tool_done","id":"t1","state":"ok","summary":"done"}""");
        Assert.NotEqual(working, card.BorderThickness);
        // And the word says it too, for anyone who cannot see the edge at all.
        Assert.Contains("done", card.StateWord);
    }

    // Where each answer came from is on the card, in words. A run that finished
    // in a second because everything was already recorded looks exactly like a
    // run that made it up, and this is the only thing that tells them apart.
    [AvaloniaFact]
    public void EveryStepSaysWhetherItAskedTheModelOrReplayedOne()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Working it out","state":"running"}""");
        run.Say("""{"event":"tool_done","id":"t1","state":"ok","summary":"done","ms":2000,"origin":"live"}""");
        run.Say("""{"event":"tool","id":"t2","title":"Working it out again","state":"running"}""");
        run.Say("""{"event":"tool_done","id":"t2","state":"ok","summary":"done","ms":0,"origin":"cache"}""");

        var cards = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().ToList();
        Assert.Contains("asked the model", cards[0].StateWord);
        Assert.Contains("from the recording", cards[1].StateWord);
        // Read aloud, the source comes with it rather than being colour or position.
        Assert.Contains("from the recording",
                        AutomationProperties.GetName(cards[1]) ?? "");
    }

    // What a run is allowed to do is said before it does anything, not worked
    // out afterwards from how fast it went.
    [AvaloniaFact]
    public void TheRunSaysUpFrontWhetherItIsAskingOrReplaying()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"run","mode":"replay","model":"claude-sonnet-5","backend":"cli","says":"from the recording only"}""");
        Assert.Contains("Running from the recording", Words(window));
        Assert.Contains("No model and no internet", Words(window));

        var (_, second, live) = Open();
        live.Say("""{"event":"run","mode":"live","model":"claude-sonnet-5","backend":"cli","says":"asks every time"}""");
        // A live run still reuses a chart already on disk, so the line must not
        // promise that nothing is reused.
        Assert.Contains("Asking the model for every binding", Words(second));
        Assert.Contains("not read again", Words(second));
    }

    // A run that ends mid-step must not leave a card claiming to still be
    // working. Something spinning forever is the window lying about the run.
    [AvaloniaFact]
    public void AStepLeftUnfinishedWhenTheRunEndsSaysSo()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Reading the control page","state":"running"}""");
        var card = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().Single();
        Assert.True(card.Running);

        run.End(1);
        window.UpdateLayout();
        Assert.False(card.Running);
        Assert.Contains("never finished", card.StateWord);
    }

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

    // A run that already said how it ended is not made to say it twice, and
    // that has to hold whatever the status line happened to say on the way in.
    [AvaloniaFact]
    public void ARunThatAlreadySaidHowItEndedIsNotContradicted()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"done","profile":"/tmp/x.csv","written":3,"errors":0,"warnings":0,"issues":[],"open":[],"untouched":[]}""");
        run.End(0);
        Assert.Contains("Written to /tmp/x.csv", Words(window));
        Assert.DoesNotContain("finished without writing anything", Words(window));

        var (_, changing, run2) = OpenChanging();
        run2.Say("""{"event":"failed","message":"the change was refused"}""");
        run2.End(1);
        Assert.Contains("the change was refused", Words(changing));
        Assert.DoesNotContain("stopped before it finished", Words(changing));
    }

    static (MainWindow main, AgentWindow window, Scripted run) OpenChanging()
    {
        var main = new MainWindow();
        main.Show();
        main.OpenPath(SavedProfile());
        var run = new Scripted();
        var window = new AgentWindow(main, root: "/nowhere", changing: true) { StartWith = _ => run };
        window.Show();
        run.Watching = window;
        Type(window, "make sprint a hard puff");
        Press(window, "Work it out");
        window.UpdateLayout();
        return (main, window, run);
    }

    static string SavedProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qcm-agent-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path,
            "Profile Name,,Walking\r\nmine.csv\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n");
        return path;
    }

    // A run can fail after it has already replaced a file. Telling someone
    // nothing was written would send them away believing a profile they now
    // depend on is untouched.
    [AvaloniaFact]
    public void AFailureAfterAWriteSaysWhatWasWritten()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"failed","message":"the diff could not be read","wrote":["/tmp/mine.csv"]}""");
        var words = Words(window);
        Assert.Contains("mine.csv", words);
        Assert.Contains("had already been written", words);
        Assert.DoesNotContain("Nothing was written", words);
    }

    // An event this version does not know is still something the agent said.
    [AvaloniaFact]
    public void AnEventThisVersionDoesNotKnowIsStillShown()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"something_new","detail":"a thing that happened"}""");
        Assert.Contains("a thing that happened", Words(window));
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

    // Installing is offered only when there is something to install onto, and
    // it hands the file to the app's own install rather than doing its own.
    [AvaloniaFact]
    public void InstallingIsOfferedOnlyWhenAQuadStickIsPluggedIn()
    {
        var (_, window, run) = Open();
        window.DeviceConnected = () => false;
        run.Say("""{"event":"done","profile":"/tmp/x.csv","written":4,"errors":0,"warnings":0,"issues":[],"open":[],"untouched":[]}""");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                              b => Label(b).Contains("Install it to your QuadStick"));

        var (_, plugged, run2) = Open();
        string? installed = null;
        plugged.DeviceConnected = () => true;
        plugged.InstallWritten = path => installed = path;
        run2.Say("""{"event":"done","profile":"/tmp/x.csv","written":4,"errors":0,"warnings":0,"issues":[],"open":[],"untouched":[]}""");
        Press(plugged, "Install it to your QuadStick");
        Assert.Equal("/tmp/x.csv", installed);
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

        Assert.Equal(new[] { "--game", "Hollow Knight Silksong", "--live" },
                     window.Arguments("Hollow Knight Silksong", null, false));
        // No profile open, so even a verb builds a game rather than silently
        // editing something that is not there.
        Assert.Equal(new[] { "--game", "make sprint a hard puff", "--live" },
                     window.Arguments("make sprint a hard puff", null, false));
        Assert.Equal(new[] { "--edit", "/tmp/mine.csv", "--request", "make sprint a hard puff",
                             "--live" },
                     window.Arguments("make sprint a hard puff", "/tmp/mine.csv", false));
        Assert.Contains("--replay", window.Arguments("Elden Ring", null, true));
        // Never both, and never neither. A run with no mode named is a run that
        // quietly reuses a recording and looks like it thought about it.
        Assert.DoesNotContain("--live", window.Arguments("Elden Ring", null, true));
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
        Assert.Equal(new[] { "--edit", "/tmp/mine.csv", "--request", "sprint should be lighter",
                             "--live" },
                     window.Arguments("sprint should be lighter", "/tmp/mine.csv", false, changing: true));
    }

    // Stopping ends the run without throwing the transcript away, so what it
    // did up to that point is still on screen to read.
    [AvaloniaFact]
    public void StoppingEndsTheRunAndKeepsWhatItSaid()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Read how Elden Ring is controlled","state":"running"}""");
        Press(window, "Stop");
        Assert.True(run.Disposed);
        Assert.Contains("Read how Elden Ring is controlled", Words(window));
        // Stop is only offered while something is running.
        Assert.False(Find(window, "Stop").IsEnabled);
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

    // ---- the device, and the walk through it ------------------------------

    // Celeste, two controls placed, one still to ask about, one deliberately
    // left. The Left hole and the Right hole are the only parts with anything
    // on them, so the walkthrough is: the whole thing, then those two, then
    // what is left to ask.
    static readonly string Map = """
        {"event":"map","game":"Celeste",
         "rows":[{"output":"kb_c","action":"Jump","inputs":["mp_right_puff"],"function":"normal",
                  "why":"51 of 96 of his profiles do this"},
                 {"output":"kb_x","action":"Dash","inputs":["mp_left_puff_soft"],"function":"toggle",
                  "why":"he does this in every platformer"}],
         "open":[{"output":"kb_left_shift","action":"Climb","question":"Climb: hold it, or press once?"}],
         "left":[{"output":"kb_f1","action":"","why":"a keyboard key with no place on a controller profile"}],
         "untouched":[]}
        """.ReplaceLineEndings(" ");

    static AgentGuide Guide(Window window) =>
        window.GetVisualDescendants().OfType<AgentGuide>().Single();

    // The profile drawn on the device, part by part, before anybody is asked
    // anything. A list of rows saying kb_x, mp_left_puff_soft is a correct
    // answer nobody can check; "Dash: soft puff" on a picture of the left hole
    // is the same answer, and it is one a person can disagree with.
    [AvaloniaFact]
    public void TheProfileIsWalkedThroughOnTheDeviceBeforeAnythingIsAsked()
    {
        var (_, window, run) = Open();
        run.Say(Map);

        var guide = Guide(window);
        Assert.Contains("Celeste, on your QuadStick", guide.Saying);
        Assert.Contains("2 controls worked out", guide.Saying);
        Assert.Contains("1 still needs you", guide.Saying);
        Assert.Contains("1 is left unbound on purpose", guide.Saying);

        // The game's own word is what the part says, not the device token.
        Assert.Equal("Left mouthpiece hole: Dash", guide.Map.TextOf("mp_left"));
        Assert.Equal("Right mouthpiece hole: Jump", guide.Map.TextOf("mp_right"));
        // A part with nothing on it says so. That is where the next thing goes.
        Assert.Contains("nothing here", guide.Map.TextOf("mp_center"));

        // Step through it: the part being talked about is the part that is lit.
        Press(window, "Next");
        Assert.Contains("Left mouthpiece hole", guide.Saying);
        Assert.Contains("Dash: soft puff", guide.Saying);
        Assert.True(guide.Map.IsLit("mp_left"));
        Assert.False(guide.Map.IsLit("mp_right"));

        Press(window, "Next");
        Assert.Contains("Jump: puff", guide.Saying);
        Assert.True(guide.Map.IsLit("mp_right"));

        // The last step says what is NOT being bound, by name and with the
        // reason. A control left on purpose and a control nobody reached are
        // different things, and neither is allowed to be a bare number.
        Press(window, "Next");
        Assert.Contains("Climb", guide.Saying);
        Assert.Contains("Climb: hold it, or press once?", guide.Saying);
        Assert.Contains("1 left unbound on purpose:", guide.Saying);
        Assert.Contains("a keyboard key with no place on a controller profile", guide.Saying);
    }

    // A question that arrives while somebody is still being shown their own
    // device waits for them. The run is blocked on the answer either way, and
    // an answer given before the tour is an answer given without it.
    [AvaloniaFact]
    public void AQuestionThatArrivesMidWalkthroughWaitsForTheWalkthrough()
    {
        var (_, window, run) = Open();
        run.Say(Map);
        run.Say(Question);

        var guide = Guide(window);
        Assert.True(guide.Walking);
        Assert.DoesNotContain("Sprint", guide.Saying);
        Assert.Empty(run.Replies);

        Press(window, "Skip the walkthrough");
        Assert.False(guide.Walking);
        Assert.Contains("Sprint: hold it, or press once to keep running?", guide.Saying);
    }

    // Each option lights the part of the mouthpiece it would land on, as it is
    // reached. Reached by keyboard as well: tabbing the options walks the
    // device, so the picture is not a mouse-only channel.
    [AvaloniaFact]
    public void EachOptionLightsThePartOfTheDeviceItWouldLandOn()
    {
        var (_, window, run) = Open();
        run.Say(Map);
        Press(window, "Skip the walkthrough");
        run.Say(Question);
        var guide = Guide(window);

        // Triple puff is a combo, the lip switch is its own part.
        Find(window, "Triple puff").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(guide.Map.IsLit("combo"));

        Find(window, "Lip, press once").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(guide.Map.IsLit("lip"));
        Assert.False(guide.Map.IsLit("combo"));
    }

    // The answer goes back as the option that was shown, and what was chosen
    // stays on screen. Answering on the device must not be a second, looser
    // path to the same write.
    [AvaloniaFact]
    public void AnAnswerOnTheDeviceGoesBackExactlyAsItWasShown()
    {
        var (_, window, run) = Open();
        run.Say(Map);
        Press(window, "Skip the walkthrough");
        run.Say(Question);

        Press(window, "Lip, press once");
        Assert.Equal("""{"id":"q1","choice":1}""", Assert.Single(run.Replies));
        Assert.Contains("You chose: lip, toggle", Guide(window).Saying);
        // And the transcript keeps it, so checking later does not depend on
        // remembering which view the decision was made in.
        Press(window, "What it did");
        Assert.Contains("You chose: lip, toggle", Words(window));
    }

    // Leaving one alone sends no choice at all. Nothing is filled in for a
    // control somebody declined to decide.
    [AvaloniaFact]
    public void LeavingOneAloneOnTheDeviceSendsNoChoice()
    {
        var (_, window, run) = Open();
        run.Say(Map);
        Press(window, "Skip the walkthrough");
        run.Say(Question);

        Press(window, "Leave this one alone");
        Assert.Equal("""{"id":"q1","choice":null}""", Assert.Single(run.Replies));
    }

    // The list to approve is read as a list, so the confirm sends the window
    // back to the transcript rather than leaving it behind a picture.
    [AvaloniaFact]
    public void TheConfirmWaitsForTheWalkthroughAndThenShowsTheList()
    {
        var (_, window, run) = Open();
        run.Say(Map);
        run.Say(Confirm);
        Assert.True(Guide(window).Walking);
        Assert.Empty(run.Replies);
        // Not drawn at all yet, so there is nothing to press by accident while
        // somebody is still being shown what they would be approving.
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                              b => Label(b).Contains("Write it"));

        Press(window, "Skip the walkthrough");
        Assert.Contains("Write 1 binding?", Words(window));
        Press(window, "Write it");
        Assert.Equal("""{"id":"c1","write":true}""", Assert.Single(run.Replies));
    }

    // The three numbers the run is about, said once, as a bar and in words.
    // Nothing here is carried by the widths alone.
    [AvaloniaFact]
    public void TheTallySaysEveryNumberInWordsAsWellAsWidths()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tally","of":67,"answered":37,"asking":6}""");
        var words = Words(window);
        Assert.Contains("67 controls this game uses", words);
        Assert.Contains("37  answered from his own profiles", words);
        Assert.Contains("6  the evidence cannot settle", words);
        Assert.Contains("24  the chart does not cover", words);
    }

    // A phase says why it is happening. Watching a run without this is watching
    // a machine work: all of it visible, none of it meaning anything.
    [AvaloniaFact]
    public void EveryPhaseSaysWhyItIsHappening()
    {
        var (_, window, run) = Open();
        run.Say("""
            {"event":"stage","key":"history","title":"What his own profiles already answer",
             "why":"A control he has bound the same way for years is already answered."}
            """.ReplaceLineEndings(" "));
        Assert.Contains("A control he has bound the same way for years is already answered.",
                        Words(window));
    }

    // The run does not end at the write. The box at the top now points at the
    // file that was just written, so the next thing said is a change to it.
    [AvaloniaFact]
    public void AFinishedRunPointsTheBoxAtWhatItWrote()
    {
        var (_, window, run) = Open();
        run.Say("""
            {"event":"done","profile":"/tmp/celeste.csv","written":12,"errors":0,"warnings":0,
             "issues":[],"open":[],"untouched":[]}
            """.ReplaceLineEndings(" "));
        run.End(0);

        Assert.Contains("Say what to change at the top", Words(window));
        var next = window.Arguments("make dash a hard puff", "/tmp/celeste.csv", replay: false,
                                    changing: true);
        Assert.Equal(new[] { "--edit", "/tmp/celeste.csv", "--request", "make dash a hard puff", "--live" },
                     next);

        // And the button says what it now does, rather than still offering to
        // set the game up again.
        Type(window, "make dash a hard puff");
        Press(window, "Change it");
        Assert.True(Find(window, "Stop").IsEnabled);
    }

    // The raw call is one click away on the card itself. It used to cost every
    // step a second row saying "what it was given, and what came back", which
    // on a forty step run is a screenful of height spent on a label.
    [AvaloniaFact]
    public void TheCardItselfOpensToShowTheRawCall()
    {
        var (_, window, run) = Open();
        run.Say("""{"event":"tool","id":"t1","title":"Reading the control page","state":"running","detail":{"url":"x"}}""");
        Assert.DoesNotContain("What it was given", Words(window));

        var card = window.GetVisualDescendants().OfType<AgentWindow.ToolCard>().Single();
        var expander = card.GetVisualDescendants().OfType<Expander>().Single();
        Assert.False(expander.IsExpanded);
        expander.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains("\"url\"", Words(window));
    }
}
