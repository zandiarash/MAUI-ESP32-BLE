using System.Text.Json;
using System.Text.Json.Serialization;
using IotStarter.App.Core.Network;
using Microsoft.Extensions.Logging;

namespace IotStarter.App.Core.Devices;

/// <summary>A device this app controls. The API token is kept separately, in secure storage.</summary>
public sealed record DeviceRecord(
    string Id,
    ushort ModelId,
    string Name,
    string? LastIPv4,
    int Port,
    DateTimeOffset? LastSeen,
    bool IsDemo)
{
    public DeviceEndpoint? LastEndpoint => LastIPv4 is null ? null : new DeviceEndpoint(LastIPv4, Port);
}

/// <summary>The local device list (ID, model, name, last IP, last seen).</summary>
public interface IDeviceStore
{
    Task<IReadOnlyList<DeviceRecord>> GetAllAsync(CancellationToken cancellationToken);

    Task UpsertAsync(DeviceRecord device, CancellationToken cancellationToken);

    Task RemoveAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>Per-device API tokens, in platform secure storage (Keystore / Keychain / DPAPI).</summary>
public interface ITokenStore
{
    Task<string?> GetAsync(string deviceId);

    Task SetAsync(string deviceId, string token);

    Task RemoveAsync(string deviceId);
}

/// <summary>Where the app may keep its files.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
}

/// <summary>JSON file store. Writes go to a temporary file first, then replace the list.</summary>
public sealed class JsonDeviceStore(IAppPaths paths, ILogger<JsonDeviceStore> logger) : IDeviceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<DeviceRecord>? _cache;

    private string FilePath => Path.Combine(paths.DataDirectory, "devices.json");

    public async Task<IReadOnlyList<DeviceRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. await LoadAsync(cancellationToken).ConfigureAwait(false)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DeviceRecord> devices = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int index = devices.FindIndex(d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                devices[index] = device;
            }
            else
            {
                devices.Add(device);
            }

            await SaveAsync(devices, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DeviceRecord> devices = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (devices.RemoveAll(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                await SaveAsync(devices, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<DeviceRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (!File.Exists(FilePath))
            {
                return _cache = [];
            }

            await using FileStream stream = File.OpenRead(FilePath);
            List<DeviceRecord>? devices = await JsonSerializer.DeserializeAsync(stream, StoreJsonContext.Default.ListDeviceRecord, cancellationToken)
                .ConfigureAwait(false);
            return _cache = devices ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt list must not lock the user out of the app; start empty and keep the bad file aside.
            logger.LogError(ex, "The device list is corrupt; starting with an empty list");
            File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
            return _cache = [];
        }
    }

    private async Task SaveAsync(List<DeviceRecord> devices, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        string temp = FilePath + ".tmp";
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, devices, StoreJsonContext.Default.ListDeviceRecord, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, FilePath, overwrite: true);
        _cache = devices;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DeviceRecord>))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
