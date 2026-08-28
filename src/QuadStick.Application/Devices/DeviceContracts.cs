using QuadStick.Format;

namespace QuadStick.Application.Devices;

public sealed record DeviceId(string Value);

public enum DeviceTransportKind
{
    MountedVolume,
    BluetoothLowEnergy,
    Serial,
    NetworkBridge,
}

[Flags]
public enum DeviceCapabilities
{
    None = 0,
    ProfileStorage = 1,
    Telemetry = 2,
    FirmwareUpdate = 4,
}

public sealed record DeviceDescriptor(
    DeviceId Id,
    string DisplayName,
    DeviceTransportKind Transport,
    DeviceCapabilities Capabilities,
    string? Location = null);

public sealed record DeviceInstallReceipt(string InstalledPath, string? BackupPath);

public interface IDeviceDiscovery
{
    Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);
    void InvalidateCache();
}

public interface IDeviceProfileStore
{
    bool IsInstallTarget(DeviceId device);

    Task<DeviceInstallReceipt> InstallAsync(
        ProfileFile profile,
        DeviceId device,
        bool confirmDefaultCsv,
        bool confirmPreferencesCsv,
        CancellationToken cancellationToken = default);
}
