namespace QuadStick.Application.Devices;

/// <summary>Opaque device identity. Only the owning Infrastructure adapter may
/// interpret Value.</summary>
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
    string? Detail = null);

public sealed record DeviceProfileId(DeviceId Device, string FileName);

public sealed record DeviceProfileEntry(DeviceProfileId Id, string FileName);

public sealed record DeviceProfileGroup(
    DeviceDescriptor Device,
    IReadOnlyList<DeviceProfileEntry> Profiles,
    string? Error = null);

public sealed record DeviceProfilePayload(string FileName, string CsvText);

public sealed record DeviceInstallRequest(
    DeviceProfilePayload Payload,
    bool AllowDefaultCsv,
    bool AllowPreferencesCsv);

public sealed record DeviceRecoveryReference(string DisplayLocation);

public sealed record DeviceInstallReceipt(
    string FileName,
    DeviceRecoveryReference? Recovery = null);

public sealed record DeviceDeleteReceipt(
    string FileName,
    DeviceRecoveryReference Recovery);

public interface IDeviceDiscovery
{
    Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);
    void InvalidateCache();
}

/// <summary>Resolves a user-selected mounted folder into an opaque device. The
/// presentation may know that a folder picker was used; it never turns that
/// path into a DeviceId itself.</summary>
public interface IManualDeviceResolver
{
    Task<DeviceDescriptor?> ResolveMountedFolderAsync(
        string folder,
        CancellationToken cancellationToken = default);
}

/// <summary>Profile-storage capability shared by mounted-volume and future
/// transports. Filesystem roots and absolute paths are adapter-private.</summary>
public interface IDeviceProfileStore
{
    Task<IReadOnlyList<DeviceProfileGroup>> ListAsync(CancellationToken cancellationToken = default);

    Task<string> ReadAsync(
        DeviceProfileId profile,
        CancellationToken cancellationToken = default);

    Task<DeviceInstallReceipt> InstallAsync(
        DeviceId device,
        DeviceInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<DeviceDeleteReceipt> DeleteAsync(
        DeviceProfileId profile,
        string recoveryDirectory,
        CancellationToken cancellationToken = default);
}