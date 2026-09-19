#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class GigaChatTokenProvider
{
    private static readonly Uri OAuthEndpoint =
        new("https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly HttpClient _client;
    private readonly string _authorizationKey;
    private readonly string _scope;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public GigaChatTokenProvider(
        HttpClient client,
        string authorizationKey,
        string scope,
        TimeProvider time)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _authorizationKey = NormalizeKey(authorizationKey);
        _scope = string.IsNullOrWhiteSpace(scope)
            ? throw new ArgumentException("GigaChat OAuth scope is required.", nameof(scope))
            : scope.Trim();
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (IsCurrent())
            return _accessToken!;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsCurrent())
                return _accessToken!;

            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthEndpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Basic", _authorizationKey);
            request.Headers.TryAddWithoutValidation("RqUID", Guid.NewGuid().ToString());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["scope"] = _scope });

            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw Error("oauth_http_error",
                    $"GigaChat OAuth returned HTTP {(int)response.StatusCode}.");

            try
            {
                await using var stream =
                    await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: ct).ConfigureAwait(false);
                var root = document.RootElement;
                var token = root.TryGetProperty("access_token", out var tokenElement)
                    ? tokenElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(token))
                    throw Error("oauth_protocol_error",
                        "GigaChat OAuth response has no access token.");

                _accessToken = token;
                _expiresAt = ReadExpiry(root);
                return token;
            }
            catch (JsonException)
            {
                throw Error("oauth_protocol_error",
                    "GigaChat OAuth returned malformed JSON.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _accessToken = null;
        _expiresAt = default;
    }

    private bool IsCurrent() =>
        !string.IsNullOrEmpty(_accessToken) &&
        _time.GetUtcNow() + RefreshMargin < _expiresAt;

    private DateTimeOffset ReadExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expires_at", out var expires) ||
            expires.ValueKind != JsonValueKind.Number ||
            !expires.TryGetInt64(out var value) ||
            value <= 0)
        {
            return _time.GetUtcNow().AddMinutes(25);
        }

        return value > 10_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException(
                "GigaChat authorization key is required.",
                nameof(key));

        var normalized = key.Trim();
        return normalized.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            ? normalized[6..].Trim()
            : normalized;
    }

    private static HarnessException Error(string code, string message) =>
        new(new HarnessError(code, message, true));
}
