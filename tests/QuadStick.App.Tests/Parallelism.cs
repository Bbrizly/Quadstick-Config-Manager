using Xunit;

// This suite drives a real window against process-wide statics: the settings
// file, MainWindow.LibraryDir, CrashGuard.RescueDirOverride,
// CrashReport.PendingDirOverride and Telemetry's consent state. A test that
// points one of those at its own temp directory is still not safe, because the
// class xUnit runs beside it points the same static somewhere else halfway
// through. That was fixed once per test and the suite went on failing about
// one run in six, in a different class each time.
//
// One at a time. The suite is a little slower and it stops lying.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
