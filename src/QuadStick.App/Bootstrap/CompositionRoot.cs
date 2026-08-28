using QuadStick.Application.Devices;
using QuadStick.Infrastructure.Devices.MountedVolume;
using QuadStick.Infrastructure.Files;
using QuadStick.Format;

namespace QuadStick.App;

/// <summary>Manual composition root. Concrete adapters are constructed here and nowhere in Application.</summary>
internal sealed class CompositionRoot
{
    public DiscoverDevicesUseCase DiscoverDevices { get; }
    public InstallProfileUseCase InstallProfile { get; }

    public CompositionRoot()
    {
        var mountedDevice = new MountedVolumeDeviceAdapter();
        DiscoverDevices = new DiscoverDevicesUseCase(mountedDevice);
        InstallProfile = new InstallProfileUseCase(mountedDevice);
    }

    internal static IReadOnlyList<string> FindDeviceRoots() => Device.FindCandidatesCached();
    internal static string DefaultDeviceBackupDirectory => Device.DefaultBackupDir();
    internal static string DeviceLabelFor(string root) => MountedVolumeDeviceFileSource.LabelFor(root);

    internal static DeviceFileManagementUseCase CreateDeviceFileManagement(
        Func<IReadOnlyList<string>> findRoots) => new(
            new MountedVolumeDeviceFileSource(findRoots),
            new PhysicalProfileLibraryStore(),
            new GoogleProfileSheetLinkResolver());
}

public partial class MainWindow
{
    readonly CompositionRoot _architectureServices = new();
}
