namespace QuadStick.App;

// ---------------------------------------------------------------------------
// DO NOT DELETE THE AGENT CODE.
//
// AgentWindow.cs, AgentGuide.cs, AgentBridge.cs, tests/QuadStick.App.Tests/
// AgentWindowTests.cs and the agent/ python pipeline are switched OFF for this
// release, not abandoned. They still build and their tests still run, which is
// how we know they will work when the switch flips back.
// ---------------------------------------------------------------------------

/// <summary>The presentation switch for the "Set up a game" and "Ask for a change"
/// agent. This belongs in App because it only controls whether UI entry points
/// are visible; the agent execution contract remains outside the presentation layer.</summary>
public static class AgentFeature
{
    // readonly, not const: a const false would make guarded code read as
    // unreachable and warnings are errors here.
    public static readonly bool Enabled = false;
}
