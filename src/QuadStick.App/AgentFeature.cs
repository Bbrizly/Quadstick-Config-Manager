namespace QuadStick.App;

// ---------------------------------------------------------------------------
// DO NOT DELETE THE AGENT CODE.
//
// AgentWindow.cs, AgentGuide.cs, AgentBridge.cs, tests/QuadStick.App.Tests/
// AgentWindowTests.cs and the agent/ python pipeline are switched OFF for this
// release, not abandoned. They still build and their tests still run, which is
// how we know they will work when the switch flips back.
//
// If you are cleaning up dead code, unused UI, or unreferenced files: this is
// not dead code. Leave every Agent* file where it is. Turning the feature back
// on is one line below. Writing it again would cost weeks.
// ---------------------------------------------------------------------------

/// <summary>The one switch for the "Set up a game" and "Ask for a change"
/// agent. Off means the two buttons that reach it are hidden and
/// <see cref="MainWindow.ShowAgent"/> does nothing. Set it to true and the
/// whole feature is back, unchanged.</summary>
public static class AgentFeature
{
    // readonly, not const: a const false would make the guarded code read as
    // unreachable and warnings are errors here.
    public static readonly bool Enabled = false;
}
