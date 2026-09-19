#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class GigaChatProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly GigaChatTokenProvider _tokens;
    private readonly string _model;
    private readonly Uri _endpoint;

    public GigaChatProvider(
        HttpClient client,
        GigaChatTokenProvider tokens,
        string model,
        Uri endpoint)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("GigaChat model is required.", nameof(model))
            : model;
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<ModelAction> NextAsync(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages,
            functions = tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = tool.Parameters
            }),
            function_call = "auto"
        }, HarnessJson.Options);

        using var response = await SendWithRetryAsync(body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Error(
                "provider_http_error",
                $"GigaChat returned HTTP {(int)response.StatusCode}.",
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500);

        try
        {
            await using var stream =
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: ct).ConfigureAwait(false);
            return ParseAction(document.RootElement, tools);
        }
        catch (JsonException)
        {
            throw Error(
                "provider_protocol_error",
                "GigaChat returned malformed JSON.",
                true);
        }
    }

    public JsonElement Feedback(ModelAction action, object result)
    {
        ArgumentNullException.ThrowIfNull(action);
        var feedback = new Dictionary<string, object?>
        {
            ["role"] = "function",
            ["name"] = action.Name,
            ["content"] = JsonSerializer.Serialize(result, HarnessJson.Options)
        };
        if (action.AssistantMessage.TryGetProperty(
                "functions_state_id",
                out var state))
        {
            feedback["functions_state_id"] = state.Clone();
        }

        return JsonSerializer.SerializeToElement(feedback, HarnessJson.Options);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string body,
        CancellationToken ct)
    {
        var retriedUnauthorized = false;
        var retriedRateLimit = false;

        while (true)
        {
            var token = await _tokens.GetAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                !retriedUnauthorized)
            {
                retriedUnauthorized = true;
                response.Dispose();
                _tokens.Invalidate();
                continue;
            }

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

    private static bool TryGetRetryDelay(
        HttpResponseMessage response,
        out TimeSpan delay)
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

    private static ModelAction ParseAction(
        JsonElement root,
        IReadOnlyList<ToolDefinition> tools)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw Error(
                "provider_protocol_error",
                "GigaChat response has no choices.",
                true);
        }

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            finishReason.ValueKind == JsonValueKind.String &&
            string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
        {
            throw Error(
                "truncated_response",
                "GigaChat response was truncated.",
                true);
        }

        if (!choice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("function_call", out var functionCall) ||
            functionCall.ValueKind != JsonValueKind.Object)
        {
            throw Error(
                "unstructured_response",
                "GigaChat response contains no native function call.",
                true);
        }

        var name = functionCall.TryGetProperty("name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
            throw Error(
                "provider_protocol_error",
                "GigaChat function call has no name.",
                true);

        var tool = tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (tool is null)
            throw Error(
                "unknown_function",
                "GigaChat requested an unknown function.",
                false);

        if (!functionCall.TryGetProperty("arguments", out var arguments) ||
            !JsonSchemaValidator.IsValid(arguments, tool.Parameters))
        {
            throw Error(
                "invalid_function_arguments",
                "GigaChat returned arguments that do not match the function schema.",
                true);
        }

        return new ModelAction(name, arguments.Clone(), message.Clone());
    }

    private static HarnessException Error(
        string code,
        string message,
        bool retryable) =>
        new(new HarnessError(code, message, retryable));

    private static class JsonSchemaValidator
    {
        public static bool IsValid(JsonElement value, JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object)
                return false;

            if (schema.TryGetProperty("enum", out var enumValues) &&
                enumValues.ValueKind == JsonValueKind.Array &&
                !enumValues.EnumerateArray().Any(item =>
                    JsonElement.DeepEquals(item, value)))
            {
                return false;
            }

            if (schema.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                !MatchesType(value, type.GetString()!))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Object => ValidObject(value, schema),
                JsonValueKind.Array => ValidArray(value, schema),
                JsonValueKind.String => ValidString(value, schema),
                JsonValueKind.Number => ValidNumber(value, schema),
                _ => true
            };
        }

        private static bool MatchesType(JsonElement value, string type) =>
            type switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number &&
                             value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            };

        private static bool ValidObject(JsonElement value, JsonElement schema)
        {
            var properties = schema.TryGetProperty("properties", out var props) &&
                             props.ValueKind == JsonValueKind.Object
                ? props
                : default;

            if (schema.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String ||
                        !value.TryGetProperty(item.GetString()!, out _))
                    {
                        return false;
                    }
                }
            }

            var rejectExtras =
                schema.TryGetProperty("additionalProperties", out var additional) &&
                additional.ValueKind == JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Object ||
                    !properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    if (rejectExtras)
                        return false;
                    continue;
                }

                if (!IsValid(property.Value, propertySchema))
                    return false;
            }

            return true;
        }

        private static bool ValidArray(JsonElement value, JsonElement schema)
        {
            if (schema.TryGetProperty("minItems", out var minItems) &&
                minItems.TryGetInt32(out var min) &&
                value.GetArrayLength() < min)
            {
                return false;
            }

            if (schema.TryGetProperty("maxItems", out var maxItems) &&
                maxItems.TryGetInt32(out var max) &&
                value.GetArrayLength() > max)
            {
                return false;
            }

            if (!schema.TryGetProperty("items", out var items))
                return true;
            return value.EnumerateArray().All(item => IsValid(item, items));
        }

        private static bool ValidString(JsonElement value, JsonElement schema)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) &&
                minLength.TryGetInt32(out var min) &&
                text.Length < min)
            {
                return false;
            }

            return !schema.TryGetProperty("maxLength", out var maxLength) ||
                   !maxLength.TryGetInt32(out var max) ||
                   text.Length <= max;
        }

        private static bool ValidNumber(JsonElement value, JsonElement schema)
        {
            if (!value.TryGetDecimal(out var number))
                return false;
            if (schema.TryGetProperty("minimum", out var minimum) &&
                minimum.TryGetDecimal(out var min) &&
                number < min)
            {
                return false;
            }

            return !schema.TryGetProperty("maximum", out var maximum) ||
                   !maximum.TryGetDecimal(out var max) ||
                   number <= max;
        }
    }
}
