namespace QuadStick.App;

/// <summary>Presentation-only compatibility shim for the common
/// Application.Current resource lookup idiom. The solution now also contains
/// a sibling QuadStick.Application namespace, so leaving the identifier
/// unqualified makes C# bind it as a namespace in many windows.</summary>
internal static class Application
{
    public static Avalonia.Application? Current => Avalonia.Application.Current;
}
