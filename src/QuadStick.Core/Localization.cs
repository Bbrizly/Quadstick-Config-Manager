using System.Resources;

// English is what Strings.resx holds, so a machine already running English
// loads no satellite assembly and does no lookup work. The app decides which
// language that is, before its first window; this library follows.
[assembly: NeutralResourcesLanguage("en")]
