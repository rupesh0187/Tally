using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TallyNext.Contracts.Auth;
using TallyNext.Contracts.Sync;

namespace TallyNext.Infrastructure.Local.Sync;

/// <summary>The configured server address. Only HTTPS is accepted, except for loopback during development.</summary>
public sealed class ServerEndpoint
{
    private Uri? _baseAddress;

    public Uri? BaseAddress => _baseAddress;

    public bool IsConfigured => _baseAddress is not null;

    public void Set(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Enter a valid server address, for example https://accounts.example.com.", nameof(url));
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new ArgumentException("The server address must use HTTPS.", nameof(url));
        }

        _baseAddress = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }
}

/// <summary>HTTPS implementation of <see cref="ISyncTransport"/> with automatic access-token refresh.</summary>
public sealed class HttpSyncTransport(HttpClient http, ServerEndpoint endpoint, DeviceTokenStore tokens) : ISyncTransport
{
    private static readonly JsonSerializerOptions Json = SyncJson.Options;

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, "api/auth/login", request), authorize: false, cancellationToken);
        return await ReadAsync<TokenResponse>(response, cancellationToken);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, "api/auth/refresh", request), authorize: false, cancellationToken);
        return await ReadAsync<TokenResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<SyncCompanyInfo>> GetCompaniesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url("api/sync/companies")), authorize: true, cancellationToken);
        return await ReadAsync<List<SyncCompanyInfo>>(response, cancellationToken);
    }

    public async Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, "api/sync/push", request), authorize: true, cancellationToken);
        return await ReadAsync<SyncPushResponse>(response, cancellationToken);
    }

    public async Task<SyncPullResponse> PullAsync(Guid companyId, long cursor, int limit, CancellationToken cancellationToken)
    {
        var path = $"api/sync/pull?companyId={companyId}&cursor={cursor}&limit={limit}";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), authorize: true, cancellationToken);
        return await ReadAsync<SyncPullResponse>(response, cancellationToken);
    }

    public async Task<ServerEntityState> GetEntityAsync(Guid companyId, string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var path = $"api/sync/entity?companyId={companyId}&entityType={Uri.EscapeDataString(entityType)}&entityId={entityId}";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), authorize: true, cancellationToken);
        return await ReadAsync<ServerEntityState>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool authorize, CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(build, authorize, forceRefresh: false, cancellationToken);
        if (authorize && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            response = await SendOnceAsync(build, authorize, forceRefresh: true, cancellationToken);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var (code, message) = await ReadProblemAsync(response, cancellationToken);
            var failure = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized when code == "auth.access_revoked" => SyncTransportFailure.AccessRevoked,
                HttpStatusCode.Unauthorized => SyncTransportFailure.Unauthorized,
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound => SyncTransportFailure.NoAccess,
                HttpStatusCode.UpgradeRequired => SyncTransportFailure.UpgradeRequired,
                HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => SyncTransportFailure.Offline,
                _ => SyncTransportFailure.ServerError,
            };
            throw new SyncTransportException(failure, message ?? $"The server answered {(int)response.StatusCode}.", code);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> build, bool authorize, bool forceRefresh, CancellationToken cancellationToken)
    {
        using var request = build();
        if (authorize)
        {
            var token = await tokens.GetAccessTokenAsync(this, forceRefresh, cancellationToken);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncTransportException(SyncTransportFailure.Offline, "The server could not be reached.", inner: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncTransportException(SyncTransportFailure.Offline, "The server did not respond in time.", inner: ex);
        }
    }

    private HttpRequestMessage JsonRequest<T>(HttpMethod method, string path, T body) => new(method, Url(path))
    {
        Content = JsonContent.Create(body, options: Json),
    };

    private Uri Url(string path) =>
        new(endpoint.BaseAddress ?? throw new SyncTransportException(SyncTransportFailure.Offline, "No server address is configured."), path);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                   ?? throw new SyncTransportException(SyncTransportFailure.ServerError, "The server returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new SyncTransportException(SyncTransportFailure.ServerError, "The server response could not be read.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncTransportException(SyncTransportFailure.Offline, "The connection was lost while reading the response.", inner: ex);
        }
    }

    private static async Task<(string? Code, string? Message)> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            return (code, title);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
