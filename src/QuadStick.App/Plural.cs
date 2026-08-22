namespace QuadStick.App;

// Every count in this app gets read aloud. "1 mode sheet(s)" is spoken as
// "one mode sheet open paren s close paren", which is the sort of thing that
// makes a screen reader user stop trusting the app to describe itself.
static class Plural
{
    public static string Of(int n, string one, string? many = null) =>
        $"{n} {(n == 1 ? one : many ?? one + "s")}";
}
