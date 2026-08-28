namespace QuadStick.Application.Devices;

public sealed class DiscoverDevicesUseCase
{
    readonly IDeviceDiscovery _discovery;

    public DiscoverDevicesUseCase(IDeviceDiscovery discovery) => _discovery = discovery;

    public Task<IReadOnlyList<DeviceDescriptor>> ExecuteAsync(CancellationToken cancellationToken = default) =>
        _discovery.DiscoverAsync(cancellationToken);

    public void InvalidateCache() => _discovery.InvalidateCache();
}
