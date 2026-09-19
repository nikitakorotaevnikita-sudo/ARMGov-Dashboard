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

public sealed class QwenProvider : IModelProvider
{
    private static readonly string[] LegacyActions = ["tool", "schema", "sql", "answer"];
    private static readonly HashSet<string> KnownActions = new(
        ToolDefinitions.All.Select(tool => tool.Name).Concat(LegacyActions),
        StringComparer.Ordinal);

    private readonly HttpClient _client;
    private readonly string _token;
    private readonly string _model;
    private readonly Uri _endpoint;

    public QwenProvider(HttpClient client, string token, string model, Uri endpoint)
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

    public async Task<ModelAction> NextAsync(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);

        var requestMessages = BuildMessages(messages, tools);
        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = requestMessages,
            max_tokens = 700,
            temperature = 0.2,
            chat_template_kwargs = new { enable_thinking = false }
        }, HarnessJson.Options);

        using var response = await SendWithRetryAsync(body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Error(
                "provider_http_error",
                $"Qwen returned HTTP {(int)response.StatusCode}.",
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500);

        string content;
        JsonElement assistantMessage;
        try
        {
            await using var stream =
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            var choice = document.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String &&
                string.Equals(finishReason.GetString(), "length", StringComparison.Ordinal))
            {
                throw Error(
                    "truncated_response",
                    "Qwen response was truncated.",
                    true);
            }

            var message = choice.GetProperty("message");
            assistantMessage = message.Clone();
            content = message.TryGetProperty("content", out var contentElement) &&
                      contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String)
            {
                content = reasoning.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            throw Error(
                "provider_protocol_error",
                "Qwen returned malformed JSON.",
                true);
        }

        return ParseAction(content, assistantMessage);
    }

    public JsonElement Feedback(ModelAction action, object result)
    {
        ArgumentNullException.ThrowIfNull(action);
        return JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = JsonSerializer.Serialize(new
            {
                tool = action.Name,
                result
            }, HarnessJson.Options)
        }, HarnessJson.Options);
    }

    private static List<object> BuildMessages(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var requestMessages = new List<object>
        {
            new { role = "system", content = BuildSystemPrompt(tools) }
        };

        foreach (var message in messages)
        {
            if (message.TryGetProperty("role", out var roleElement) &&
                roleElement.ValueKind == JsonValueKind.String &&
                message.TryGetProperty("content", out var contentElement))
            {
                var role = roleElement.GetString();
                if (role is "user" or "assistant" or "system")
                {
                    requestMessages.Add(new
                    {
                        role,
                        content = contentElement.ValueKind == JsonValueKind.String
                            ? contentElement.GetString()
                            : contentElement.GetRawText()
                    });
                }
            }
        }

        return requestMessages;
    }

    private static string BuildSystemPrompt(IReadOnlyList<ToolDefinition> tools)
    {
        var catalog = string.Join(
            "\n",
            tools.Select(tool =>
                $"- {tool.Name}: {tool.Description} schema={tool.Parameters.GetRawText()}"));
        return
            "Ты — аналитик процессов. Каждый ответ — РОВНО ОДИН JSON-объект без текста вокруг.\n" +
            "Используй поле action с именем инструмента и аргументами на верхнем уровне.\n" +
            "Старый синтаксис tool/schema/sql/answer допустим только как совместимость.\n" +
            "answer без валидного report не считается завершением.\n\n" +
            "Инструменты:\n" + catalog;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string body,
        CancellationToken ct)
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

    public static ModelAction ParseAction(string content, JsonElement assistantMessage)
    {
        var parsed = ExtractLastKnownAction(content);
        if (parsed is null)
            throw Error(
                "provider_protocol_error",
                "Qwen response contains no recognizable action JSON.",
                true);

        var (toolName, arguments) = NormalizeAction(parsed.Value);
        var validation = ToolDefinitions.Validate(toolName, arguments);
        if (!validation.Ok)
            throw new HarnessException(validation.Errors[0]);

        return new ModelAction(toolName, arguments, assistantMessage);
    }

    private static JsonElement? ExtractLastKnownAction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = StripCodeFence(text);
        JsonElement? found = null;
        var start = normalized.IndexOf('{');
        while (start >= 0)
        {
            if (TryExtractBalancedObject(normalized, start, out var fragment))
            {
                try
                {
                    var element = JsonDocument.Parse(fragment).RootElement.Clone();
                    if (element.ValueKind == JsonValueKind.Object &&
                        element.TryGetProperty("action", out var actionValue) &&
                        actionValue.ValueKind == JsonValueKind.String &&
                        KnownActions.Contains(actionValue.GetString()!))
                    {
                        found = element;
                    }
                }
                catch (JsonException)
                {
                    // truncated or invalid fragment — keep scanning
                }
            }

            start = normalized.IndexOf('{', start + 1);
        }

        return found;
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
            return trimmed;

        var body = trimmed[(firstLineEnd + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing].Trim() : body.Trim();
    }

    private static bool TryExtractBalancedObject(string text, int start, out string fragment)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (ch == '{')
                depth++;
            else if (ch == '}' && --depth == 0)
            {
                fragment = text.Substring(start, i - start + 1);
                return true;
            }
        }

        fragment = "";
        return false;
    }

    private static (string ToolName, JsonElement Arguments) NormalizeAction(JsonElement action)
    {
        var actionName = action.GetProperty("action").GetString()!;
        if (!LegacyActions.Contains(actionName))
            return (actionName, ExtractDirectArguments(actionName, action));

        return actionName switch
        {
            "sql" => (
                "execute_sql",
                JsonSerializer.SerializeToElement(new
                {
                    sql = GetString(action, "query"),
                    parameters = EmptyObject(),
                    metricId = GetOptionalString(action, "metricId") ?? ""
                }, HarnessJson.Options)),
            "tool" => (
                "dashboard_metric",
                JsonSerializer.SerializeToElement(new
                {
                    name = GetString(action, "tool"),
                    args = action.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                        ? args
                        : EmptyObject()
                }, HarnessJson.Options)),
            "schema" => NormalizeSchemaAction(action),
            "answer" => NormalizeAnswerAction(action),
            _ => throw Error(
                "unknown_function",
                "Qwen requested an unknown action.",
                false)
        };
    }

    private static (string, JsonElement) NormalizeSchemaAction(JsonElement action)
    {
        var table = GetString(action, "table");
        var parts = table.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "public";
        var name = parts.Length == 2 ? parts[1] : parts[0];
        var fields = action.TryGetProperty("fields", out var fieldsElement) &&
                     fieldsElement.ValueKind == JsonValueKind.Array
            ? fieldsElement
            : JsonSerializer.SerializeToElement(Array.Empty<string>(), HarnessJson.Options);

        return (
            "describe_table",
            JsonSerializer.SerializeToElement(new
            {
                schema,
                table = name,
                fields
            }, HarnessJson.Options));
    }

    private static (string, JsonElement) NormalizeAnswerAction(JsonElement action)
    {
        if (!action.TryGetProperty("report", out var report) ||
            report.ValueKind != JsonValueKind.Object)
        {
            throw Error(
                "invalid_function_arguments",
                "Legacy answer requires a valid report object.",
                true);
        }

        return (
            "submit_report",
            JsonSerializer.SerializeToElement(new { report }, HarnessJson.Options));
    }

    private static JsonElement ExtractDirectArguments(string toolName, JsonElement action)
    {
        var tool = ToolDefinitions.All.First(candidate =>
            string.Equals(candidate.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
            throw Error("unknown_function", "Qwen requested an unknown tool.", false);

        var properties = tool.Parameters.GetProperty("properties");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "action", "thought" };
        foreach (var property in properties.EnumerateObject())
            allowed.Add(property.Name);

        foreach (var property in action.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Error(
                    "invalid_function_arguments",
                    "Qwen action contains unsupported properties.",
                    true);
            }
        }

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (action.TryGetProperty(property.Name, out var value))
                payload[property.Name] = value.Clone();
        }

        return JsonSerializer.SerializeToElement(payload, HarnessJson.Options);
    }

    private static JsonElement EmptyObject() =>
        JsonSerializer.SerializeToElement(new { }, HarnessJson.Options);

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HarnessException Error(string code, string message, bool retryable) =>
        new(new HarnessError(code, message, retryable));
}
