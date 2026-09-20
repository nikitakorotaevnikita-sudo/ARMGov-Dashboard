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
    private const int MaxFeedbackBytes = 1500;
    private const int MaxRequestBytes = 18000;

    private const string SystemPrompt =
        "Ты — аналитик процессов Directum RX. Отвечай ТОЛЬКО через function_call " +
        "к переданным функциям. Обычный текст без function_call запрещён и не станет отчётом.\n" +
        "Порядок: search_catalog → set_context → dashboard_metric или execute_sql → submit_report. " +
        "read_result только после успешного dashboard_metric/execute_sql, когда уже есть resultId. " +
        "Без set_context нельзя вызывать execute_sql/dashboard_metric/submit_report. " +
        "Допустимые metricId: generic_query, personal_instruction_count, completed_assignment_count, " +
        "overdue_assignment_kpi, appeal_topics, execution_discipline. Не придумывай другие metricId. " +
        "Для периода «за N месяцев» используй period={kind:months,months:N}; " +
        "kind=range используй только с точными from и to ISO8601. " +
        "search_catalog.query — короткая фраза до 5 слов (например «обращения темы»). " +
        "Для сравнения количества поручений сотрудников: найди каждого через find_employees; " +
        "при нескольких кандидатах вызови clarify; затем set_context(personal_instruction_count), " +
        "execute_sql с фильтром по каждому serverParameter из find_employees " +
        "(значение параметра в parameters не передавай) и submit_report. " +
        "Для «затор/перегруз» после set_context(overdue_assignment_kpi) зови dashboard_metric name=stuck, затем leaders. " +
        "Для «вопросы в обращениях / топ тем» после set_context(appeal_topics) зови dashboard_metric name=appeal_topics, затем сразу submit_report. " +
        "Для «исполнительская дисциплина / тренд за год» set_context(execution_discipline, period kind=months months=12), затем dashboard_metric name=execution_discipline args.period=year, затем submit_report. " +
        "В title, commentary и textTemplates не пиши цифры — числа только в таблице блоков. " +
        "Если вопрос неоднозначен по сотруднику — clarify.\n" +
        "Числа и факты бери только из результатов функций, ничего не выдумывай.";

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

        var body = BuildRequestBody(messages, tools);
        using var response = await SendWithRetryAsync(body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            if (status == 413)
            {
                throw Error(
                    "provider_payload_too_large",
                    $"GigaChat returned HTTP 413: requestBytes={Encoding.UTF8.GetByteCount(body)}. " +
                    "Соберите submit_report по уже полученным resultId без новых тяжёлых выборок.",
                    true);
            }

            var detail = status == 422
                ? await SafeBodyAsync(response, ct).ConfigureAwait(false)
                : "";
            throw Error(
                "provider_http_error",
                string.IsNullOrWhiteSpace(detail)
                    ? $"GigaChat returned HTTP {status}."
                    : $"GigaChat returned HTTP {status}: {Trunc(detail, 300)}",
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                status >= 500);
        }

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
        // GigaChat: role=function — только name + content (JSON-строка).
        // functions_state_id оставляем на assistant-сообщении; на function его нет в API.
        var content = JsonSerializer.Serialize(result, HarnessJson.Options);
        if (Encoding.UTF8.GetByteCount(content) > MaxFeedbackBytes)
        {
            content = JsonSerializer.Serialize(new
            {
                truncated = true,
                tool = action.Name,
                preview = Trunc(content, MaxFeedbackBytes / 2),
                hint = "Полные строки доступны через read_result по resultId."
            }, HarnessJson.Options);
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["role"] = "function",
            ["name"] = action.Name,
            ["content"] = content
        }, HarnessJson.Options);
    }

    private string BuildRequestBody(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var requestMessages = BuildMessages(messages, compactFunctionContent: false);
        var body = SerializeRequest(requestMessages, tools);
        if (Encoding.UTF8.GetByteCount(body) <= MaxRequestBytes)
            return body;

        requestMessages = BuildMessages(messages, compactFunctionContent: true);
        return SerializeRequest(requestMessages, tools);
    }

    private string SerializeRequest(
        List<object> requestMessages,
        IReadOnlyList<ToolDefinition> tools) =>
        JsonSerializer.Serialize(new
        {
            model = _model,
            messages = requestMessages,
            functions = tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                // Полная схема submit_report раздувает запрос до HTTP 413 у GigaChat.
                parameters = CompactParameters(tool)
            }),
            function_call = "auto",
            max_tokens = 2048
        }, HarnessJson.Options);

    private static JsonElement CompactParameters(ToolDefinition tool)
    {
        if (!string.Equals(tool.Name, "submit_report", StringComparison.Ordinal))
            return tool.Parameters;

        return JsonDocument.Parse("""
            {
              "type":"object",
              "properties":{
                "report":{
                  "type":"object",
                  "properties":{
                    "title":{"type":"string"},
                    "interpretation":{"type":"object","properties":{},"additionalProperties":true},
                    "blocks":{"type":"array"},
                    "facts":{"type":"array"},
                    "textTemplates":{"type":"array"},
                    "commentary":{"type":"string"}
                  },
                  "additionalProperties":true
                }
              },
              "required":["report"],
              "additionalProperties":false
            }
            """).RootElement.Clone();
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
            var finish = choice.TryGetProperty("finish_reason", out var fr) &&
                         fr.ValueKind == JsonValueKind.String
                ? fr.GetString()
                : null;
            var content = message.ValueKind == JsonValueKind.Object &&
                          message.TryGetProperty("content", out var c) &&
                          c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            var detail = string.IsNullOrWhiteSpace(content)
                ? $"finish_reason={finish ?? "?"}"
                : $"finish_reason={finish ?? "?"}; content={Trunc(content!, 160)}";
            throw Error(
                "unstructured_response",
                "GigaChat response contains no native function call. " + detail,
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

        if (!functionCall.TryGetProperty("arguments", out var rawArguments))
        {
            throw Error(
                "invalid_function_arguments",
                $"GigaChat returned no arguments for '{name}'.",
                true);
        }

        JsonElement arguments;
        try
        {
            arguments = NormalizeArguments(rawArguments, tool.Parameters);
        }
        catch (JsonException)
        {
            throw Error(
                "invalid_function_arguments",
                $"GigaChat returned non-JSON arguments for '{name}': {Trunc(rawArguments.GetRawText(), 180)}",
                true);
        }

        // submit_report на проводе ужат; полная проверка — в ReportValidator.
        var schemaForCheck = string.Equals(name, "submit_report", StringComparison.Ordinal)
            ? CompactParameters(tool)
            : tool.Parameters;
        if (!JsonSchemaValidator.IsValid(arguments, schemaForCheck))
        {
            throw Error(
                "invalid_function_arguments",
                $"GigaChat returned arguments that do not match the function schema for '{name}': {Trunc(arguments.GetRawText(), 220)}",
                true);
        }

        return new ModelAction(name, arguments.Clone(), message.Clone());
    }

    /// <summary>
    /// GigaChat иногда отдаёт arguments строкой JSON, null в optional-полях
    /// и числа как строки — без нормализации схема падает на валидном по смыслу вызове.
    /// </summary>
    internal static JsonElement NormalizeArguments(JsonElement raw, JsonElement schema)
    {
        var value = raw;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return value;
            using var parsed = JsonDocument.Parse(text);
            value = parsed.RootElement.Clone();
        }

        return CoerceToSchema(value, schema);
    }

    private static JsonElement CoerceToSchema(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return value.Clone();

        var expectedType = schema.TryGetProperty("type", out var typeElement) &&
                           typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        if ((expectedType is "integer" or "number") &&
            value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number))
        {
            if (expectedType == "integer" && number == Math.Truncate(number) &&
                number >= long.MinValue && number <= long.MaxValue)
            {
                return JsonSerializer.SerializeToElement((long)number, HarnessJson.Options);
            }

            if (expectedType == "number")
                return JsonSerializer.SerializeToElement(number, HarnessJson.Options);
        }

        if (value.ValueKind == JsonValueKind.Array &&
            schema.TryGetProperty("items", out var itemsSchema))
        {
            var items = value.EnumerateArray()
                .Select(item => CoerceToSchema(item, itemsSchema))
                .ToArray();
            return JsonSerializer.SerializeToElement(items, HarnessJson.Options);
        }

        if (value.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return value.Clone();
        }

        var obj = new Dictionary<string, JsonElement>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (properties.TryGetProperty(property.Name, out var propertySchema))
                obj[property.Name] = CoerceToSchema(property.Value, propertySchema);
            else
                obj[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(obj, HarnessJson.Options);
    }

    private static List<object> BuildMessages(
        IReadOnlyList<JsonElement> messages,
        bool compactFunctionContent)
    {
        var requestMessages = new List<object>
        {
            new { role = "system", content = SystemPrompt }
        };
        foreach (var message in messages)
        {
            if (compactFunctionContent &&
                message.TryGetProperty("role", out var role) &&
                role.ValueKind == JsonValueKind.String &&
                role.GetString() == "function" &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String &&
                Encoding.UTF8.GetByteCount(content.GetString() ?? "") > 800)
            {
                var name = message.TryGetProperty("name", out var nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : "tool";
                requestMessages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "function",
                    ["name"] = name,
                    ["content"] = JsonSerializer.Serialize(new
                    {
                        truncated = true,
                        hint = "Результат уже получен ранее; используйте resultId/read_result или submit_report."
                    }, HarnessJson.Options)
                });
                continue;
            }

            requestMessages.Add(message);
        }

        return requestMessages;
    }

    private static HarnessException Error(
        string code,
        string message,
        bool retryable) =>
        new(new HarnessError(code, message, retryable));

    private static async Task<string> SafeBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";

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
