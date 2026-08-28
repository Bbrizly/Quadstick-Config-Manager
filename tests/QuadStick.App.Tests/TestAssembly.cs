using Xunit;

// App tests exercise the real Avalonia application plus intentionally global
// process state (telemetry consent/client, crash-report directory overrides,
// settings test seams). Running those fixtures concurrently lets one test reset
// state while another is asserting it, which produced OS/timing-dependent CI
// failures rather than product failures. Keep Core/integration tests parallel;
// serialize only this UI/process-state assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
