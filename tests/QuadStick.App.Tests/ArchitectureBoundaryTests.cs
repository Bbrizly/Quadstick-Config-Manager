using System.Reflection;
using Xunit;

namespace QuadStick.App.Tests;

public sealed class ArchitectureBoundaryTests
{
    static readonly Assembly Core = typeof(QuadStick.Format.ProfileFile).Assembly;
    static readonly Assembly Application = typeof(QuadStick.Application.Backup.DriveBackupWorkflow).Assembly;
    static readonly Assembly Infrastructure = typeof(QuadStick.Infrastructure.Settings.JsonAppSettingsStore).Assembly;
    static readonly Assembly App = typeof(MainWindow).Assembly;

    [Fact]
    public void Core_DoesNotReferenceHigherLayersOrUiProviders()
    {
        var refs = ReferenceNames(Core);

        Assert.DoesNotContain("QuadStick.Application", refs);
        Assert.DoesNotContain("QuadStick.Infrastructure", refs);
        Assert.DoesNotContain(App.GetName().Name!, refs);
        Assert.DoesNotContain(refs, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, name => name.StartsWith("PostHog", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_ReferencesOnlyCoreAmongProductionLayers()
    {
        var quadStickRefs = ProductionLayerReferences(Application);

        Assert.Equal(new[] { "QuadStick.Core" }, quadStickRefs);
    }

    [Fact]
    public void Infrastructure_NeverReferencesPresentation()
    {
        var refs = ReferenceNames(Infrastructure);

        Assert.DoesNotContain(App.GetName().Name!, refs);
        Assert.DoesNotContain(refs, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_CannotMasqueradeAsAppNamespace()
    {
        var offenders = Infrastructure.GetTypes()
            .Where(type => IsInNamespace(type.Namespace, "QuadStick.App"))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_CannotMasqueradeAsOuterLayers()
    {
        var offenders = Application.GetTypes()
            .Where(type => IsInNamespace(type.Namespace, "QuadStick.App")
                        || IsInNamespace(type.Namespace, "QuadStick.Infrastructure"))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    static bool IsInNamespace(string? candidate, string root) =>
        candidate is not null
        && (string.Equals(candidate, root, StringComparison.Ordinal)
            || candidate.StartsWith(root + ".", StringComparison.Ordinal));

    static string[] ReferenceNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    static string[] ProductionLayerReferences(Assembly assembly) =>
        ReferenceNames(assembly)
            .Where(name => name is "QuadStick.Core" or "QuadStick.Application" or "QuadStick.Infrastructure"
                || name == App.GetName().Name)
            .ToArray();
}