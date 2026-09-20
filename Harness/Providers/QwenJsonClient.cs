#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class QwenJsonClient : IQwenJsonClient
{
    private readonly HttpClient _client;
    private readonly string _token;
    private readonly string _model;
    private readonly Uri _endpoint;

    public QwenJsonClient(HttpClient client, string token, string model, Uri endpoint)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _token = string.IsNullOrWhiteSpace(token)
            ? throw new ArgumentException("Qwen token is required.", nameof(token))
            : token;
        _model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("Qwen model is required.", nameof(model))
            : model;
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<T> CompleteAsync<T>(
        string systemPrompt,
        object input,
        int maxTokens,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(input);

        var serializedInput = JsonSerializer.Serialize(input, HarnessJson.Options);
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = serializedInput }
            },
            max_tokens = maxTokens,
            temperature = 0.2,
            chat_template_kwargs = new { enable_thinking = false }
        }, HarnessJson.Options);

        using var response = await SendWithRetryAsync(body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await HttpErrorAsync(response, ct).ConfigureAwait(false);

        var content = await ReadContentAsync(response, ct).ConfigureAwait(false);
        try
        {
            var json = JsonObjectExtractor.Extract(content);
            var value = JsonSerializer.Deserialize<T>(json, HarnessJson.Options);
            return value ?? throw new HarnessException(new HarnessError(
                "provider_protocol_error",
                $"Qwen returned null for {typeof(T).Name}.",
                true));
        }
        catch (HarnessException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ProtocolError("Qwen returned malformed JSON.");
        }
        catch (NotSupportedException)
        {
            throw ProtocolError("Qwen returned an unsupported JSON payload.");
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string body, CancellationToken ct)
    {
        var retriedRateLimit = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                !retriedRateLimit &&
                TryGetRetryDelay(response, out var delay))
            {
                retriedRateLimit = true;
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private static async Task<string> ReadContentAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw ProtocolError("Qwen returned no choices.");
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String &&
                string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
            {
                throw new HarnessException(new HarnessError(
                    "truncated_response",
                    "Qwen response was truncated.",
                    true));
            }

            if (!choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolError("Qwen returned no assistant message.");
            }

            var content = message.TryGetProperty("content", out var contentElement) &&
                          contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String)
            {
                content = reasoning.GetString() ?? "";
            }

            return content;
        }
        catch (HarnessException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ProtocolError("Qwen returned malformed JSON.");
        }
    }

    private static async Task<HarnessException> HttpErrorAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var detail = response.Content is null
            ? ""
            : SanitizeDetail(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var message = $"Qwen returned HTTP {(int)response.StatusCode}.";
        if (detail.Length > 0)
            message += $" Detail: {detail}";

        return new HarnessException(new HarnessError(
            "provider_http_error",
            message,
            response.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500));
    }

    private static string SanitizeDetail(string detail)
    {
        var normalized = new StringBuilder(detail.Length);
        var previousWhitespace = false;
        foreach (var character in detail)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!previousWhitespace)
                    normalized.Append(' ');
                previousWhitespace = true;
                continue;
            }

            normalized.Append(character);
            previousWhitespace = false;
        }

        var sanitized = normalized.ToString().Trim();
        return sanitized.Length <= 300 ? sanitized : sanitized[..300];
    }

    private static bool TryGetRetryDelay(HttpResponseMessage response, out TimeSpan delay)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            delay = delta;
            return true;
        }

        if (retry?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
            return true;
        }

        delay = default;
        return false;
    }

    private static HarnessException ProtocolError(string message) =>
        new(new HarnessError("provider_protocol_error", message, true));
}
