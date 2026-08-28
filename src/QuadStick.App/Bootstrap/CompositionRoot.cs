using QuadStick.Application.Community;
using QuadStick.Application.Devices;
using QuadStick.Infrastructure.Community;
using QuadStick.Infrastructure.Devices.MountedVolume;
using QuadStick.Infrastructure.Files;
using QuadStick.Format;

namespace QuadStick.App;

/// <summary>Manual composition root. Concrete adapters are constructed here and nowhere in Application.</summary>
internal sealed class CompositionRoot
{
    readonly MountedVolumeDeviceAdapter _mountedDevice;
    readonly HttpClient _communityHttp;

    public DiscoverDevicesUseCase DiscoverDevices { get; }
    public InstallProfileUseCase InstallProfile { get; }
    public IManualDeviceResolver ManualDevices => _mountedDevice;
    public CommunityCatalogUseCase CommunityCatalog { get; }

    public CompositionRoot()
    {
        _mountedDevice = new MountedVolumeDeviceAdapter();
        DiscoverDevices = new DiscoverDevicesUseCase(_mountedDevice);
        InstallProfile = new InstallProfileUseCase(_mountedDevice);

        // One production client for the app lifetime. The catalog source owns
        // request policy/cache semantics; presentation receives only the use case.
        _communityHttp = HttpCommunityCatalogSource.CreateProductionClient();
        CommunityCatalog = new CommunityCatalogUseCase(
            new HttpCommunityCatalogSource(_communityHttp));
    }

    internal static IReadOnlyList<string> FindDeviceRoots() => Device.FindCandidatesCached();
    internal static string DefaultDeviceBackupDirectory => Device.DefaultBackupDir();
    internal static string DeviceLabelFor(string root) => MountedVolumeDeviceAdapter.LabelFor(root);

    internal static DeviceFileManagementUseCase CreateDeviceFileManagement(
        Func<IReadOnlyList<string>> findRoots,
        Func<string> recoveryDirectory)
    {
        var mounted = new MountedVolumeDeviceAdapter(findRoots, recoveryDirectory);
        return new DeviceFileManagementUseCase(
            mounted,
            mounted,
            new PhysicalProfileLibraryStore());
    }

    internal static string? LinkedGoogleSheetUrl(ProfileFile profile) =>
        SheetsUrl.TryGetEditUrlFromHeader(
            profile.Document.HeaderVersion,
            profile.Document.HeaderSource,
            out var url) ? url : null;
}

public partial class MainWindow
{
    readonly CompositionRoot _architectureServices = new();
    internal CommunityCatalogUseCase CommunityCatalog => _architectureServices.CommunityCatalog;
}
