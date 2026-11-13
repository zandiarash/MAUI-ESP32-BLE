using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IotStarter.Shared;

namespace IotStarter.App.Core.Network;

/// <summary>What went wrong talking to a device over the network.</summary>
public enum DeviceApiError
{
    /// <summary>No answer (timeout, refused, unreachable host).</summary>
    Unreachable,

    /// <summary>401: the stored token is not accepted (device was reset or belongs to another phone).</summary>
    Unauthorized,

    /// <summary>404: unknown output or route.</summary>
    NotFound,

    /// <summary>400: the device refused the request body.</summary>
    BadRequest,

    /// <summary>Unexpected status code or malformed JSON.</summary>
    Protocol
}

public sealed class DeviceApiException(DeviceApiError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public DeviceApiError Error { get; } = error;
}

/// <summary>Where a device answers: IPv4 address and HTTP port.</summary>
public sealed record DeviceEndpoint(string IPv4, int Port = ProtocolConstants.HttpPort)
{
    public override string ToString() => Port == 80 ? IPv4 : $"{IPv4}:{Port}";
}

/// <summary>The local HTTP API (docs/protocol.md §7). Every call has a timeout and a cancellation token.</summary>
public interface IDeviceApiClient
{
    Task<DeviceInfoResponse> GetInfoAsync(DeviceEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutputResponse>> GetOutputsAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken);

    Task<OutputResponse> SetOutputAsync(DeviceEndpoint endpoint, string token, int outputId, bool on, CancellationToken cancellationToken);

    Task<string> SetNameAsync(DeviceEndpoint endpoint, string token, string name, CancellationToken cancellationToken);

    Task IdentifyAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken);

    Task RebootAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken);

    Task FactoryResetAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken);
}

/// <summary>HttpClient-based implementation. The token goes only into the ApiKey header and is never logged.</summary>
public sealed class DeviceApiClient(HttpClient http) : IDeviceApiClient
{
    /// <summary>Default timeout for calls that do not take one explicitly.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public Task<DeviceInfoResponse> GetInfoAsync(DeviceEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(endpoint, HttpMethod.Get, HttpContract.PathInfo, token: null, body: null, ApiJsonContext.Default.DeviceInfoResponse, timeout, cancellationToken);

    public async Task<IReadOnlyList<OutputResponse>> GetOutputsAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken)
    {
        OutputsResponse response = await SendAsync(endpoint, HttpMethod.Get, HttpContract.PathOutputs, token, body: null,
            ApiJsonContext.Default.OutputsResponse, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return response.Outputs ?? throw new DeviceApiException(DeviceApiError.Protocol, "The outputs list is missing.");
    }

    public Task<OutputResponse> SetOutputAsync(DeviceEndpoint endpoint, string token, int outputId, bool on, CancellationToken cancellationToken) =>
        SendAsync(endpoint, HttpMethod.Put, HttpContract.PathOutputPrefix + outputId, token,
            JsonContent.Create(new SetOutputRequest(on), ApiJsonContext.Default.SetOutputRequest),
            ApiJsonContext.Default.OutputResponse, DefaultTimeout, cancellationToken);

    public async Task<string> SetNameAsync(DeviceEndpoint endpoint, string token, string name, CancellationToken cancellationToken)
    {
        NameResponse response = await SendAsync(endpoint, HttpMethod.Put, HttpContract.PathName, token,
            JsonContent.Create(new SetNameRequest(name), ApiJsonContext.Default.SetNameRequest),
            ApiJsonContext.Default.NameResponse, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return response.Name;
    }

    public Task IdentifyAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        SendWithoutBodyAsync(endpoint, HttpContract.PathIdentify, token, cancellationToken);

    public Task RebootAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        SendWithoutBodyAsync(endpoint, HttpContract.PathReboot, token, cancellationToken);

    public Task FactoryResetAsync(DeviceEndpoint endpoint, string token, CancellationToken cancellationToken) =>
        SendWithoutBodyAsync(endpoint, HttpContract.PathFactoryReset, token, cancellationToken);

    private async Task SendWithoutBodyAsync(DeviceEndpoint endpoint, string path, string token, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        using HttpRequestMessage request = CreateRequest(endpoint, HttpMethod.Post, path, token, body: null);
        using HttpResponseMessage response = await ExecuteAsync(request, timeout.Token, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    private async Task<T> SendAsync<T>(
        DeviceEndpoint endpoint,
        HttpMethod method,
        string path,
        string? token,
        HttpContent? body,
        JsonTypeInfo<T> typeInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using HttpRequestMessage request = CreateRequest(endpoint, method, path, token, body);
        using HttpResponseMessage response = await ExecuteAsync(request, timeoutSource.Token, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);

        try
        {
            T? value = await response.Content.ReadFromJsonAsync(typeInfo, timeoutSource.Token).ConfigureAwait(false);
            return value ?? throw new DeviceApiException(DeviceApiError.Protocol, "The device sent an empty response.");
        }
        catch (JsonException ex)
        {
            throw new DeviceApiException(DeviceApiError.Protocol, "The device sent malformed JSON.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceApiException(DeviceApiError.Unreachable, "The device stopped answering.", ex);
        }
    }

    private static HttpRequestMessage CreateRequest(DeviceEndpoint endpoint, HttpMethod method, string path, string? token, HttpContent? body)
    {
        var request = new HttpRequestMessage(method, new Uri($"http://{endpoint.IPv4}:{endpoint.Port}{path}"))
        {
            Content = body
        };

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(HttpContract.ApiKeyHeader, token);
        }

        return request;
    }

    private async Task<HttpResponseMessage> ExecuteAsync(HttpRequestMessage request, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        {
            throw new DeviceApiException(DeviceApiError.Unreachable, "The device did not answer in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DeviceApiException(DeviceApiError.Unreachable, "The device could not be reached.", ex);
        }
        catch (SocketException ex)
        {
            throw new DeviceApiException(DeviceApiError.Unreachable, "The device could not be reached.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new DeviceApiException(DeviceApiError.Unauthorized, "The device did not accept this phone's key."),
            HttpStatusCode.NotFound => new DeviceApiException(DeviceApiError.NotFound, "The device does not know this output or route."),
            HttpStatusCode.BadRequest => new DeviceApiException(DeviceApiError.BadRequest, "The device refused the request."),
            _ => new DeviceApiException(DeviceApiError.Protocol, $"Unexpected status {(int)response.StatusCode}.")
        };
    }
}
