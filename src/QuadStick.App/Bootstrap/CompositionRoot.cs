using QuadStick.Application.Devices;
using QuadStick.Infrastructure.Devices.MountedVolume;

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
}

public partial class MainWindow
{
    readonly CompositionRoot _architectureServices = new();
}
