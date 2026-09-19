#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using ArmGov.Harness;

public static class QwenTests
{
    public static async Task LegacySqlNormalizesLikeGigaChat()
    {
        const string sql = "select count(*) n from public.sungero_wf_task";
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"sql","query":"select count(*) n from public.sungero_wf_task","purpose":"Проверка","metricId":"personal_instruction_count"}
            """))));

        var qwen = await provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None);
        var gigachat = await GigachatAction(sql);

        Check.Equal(gigachat.Name, qwen.Name);
        Check.Equal(gigachat.Arguments.GetProperty("sql").GetString(), qwen.Arguments.GetProperty("sql").GetString());
        Check.Equal("{}", qwen.Arguments.GetProperty("parameters").GetRawText());
        Check.Equal("personal_instruction_count", qwen.Arguments.GetProperty("metricId").GetString());
    }

    public static async Task LegacySqlMissingMetricIdIsRejected()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"sql","query":"select count(*) n from public.sungero_wf_task","purpose":"Проверка"}
            """))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None));

        Check.Equal("invalid_function_arguments", error.Error.Code);
    }

    public static async Task TwoQuotedJsonsTakeLastValidAction()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            Шаблон: {"thought":"…","action":"answer","text":"итоговый ответ"}
            Ещё шаблон: {"thought":"…","action":"sql","query":"select 0"}
            {"action":"execute_sql","sql":"select 1","parameters":{},"metricId":"personal_instruction_count"}
            """))));

        var action = await provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None);

        Check.Equal("execute_sql", action.Name);
        Check.Equal("select 1", action.Arguments.GetProperty("sql").GetString());
    }

    public static async Task TruncatedJsonIsProtocolError()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"execute_sql","sql":"select 1","parameters":{},"metricId":"personal_instruction_count"
            """))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None));

        Check.Equal("provider_protocol_error", error.Error.Code);
    }

    public static async Task JsonFenceWithExplanation()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            Кратко: нужен SQL.
            ```json
            {"action":"execute_sql","sql":"select 2","parameters":{},"metricId":"personal_instruction_count"}
            ```
            """))));

        var action = await provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None);

        Check.Equal("execute_sql", action.Name);
        Check.Equal("select 2", action.Arguments.GetProperty("sql").GetString());
    }

    public static async Task UnknownActionIsRejected()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"drop_database","sql":"select 1"}
            """))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None));

        Check.Equal("provider_protocol_error", error.Error.Code);
    }

    public static async Task ExtraConfigPropertyIsRejected()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"execute_sql","sql":"select 1","parameters":{},"metricId":"personal_instruction_count","config":{"url":"http://evil"}}
            """))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None));

        Check.Equal("invalid_function_arguments", error.Error.Code);
    }

    public static async Task ToolArgumentTypeIsRejected()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"read_result","resultId":"r1","offset":"0","take":5}
            """))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None));

        Check.Equal("invalid_function_arguments", error.Error.Code);
    }

    public static async Task EnableThinkingIsDisabledInRequest()
    {
        var handler = Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"execute_sql","sql":"select 3","parameters":{},"metricId":"personal_instruction_count"}
            """)));
        var provider = Provider(handler);

        await provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None);

        var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Check.Equal(false, body.RootElement
            .GetProperty("chat_template_kwargs")
            .GetProperty("enable_thinking")
            .GetBoolean());
    }

    public static async Task FeedbackUsesUserEnvelope()
    {
        var provider = Provider(Handler((_, _) => Json(HttpStatusCode.OK, Text("""
            {"action":"execute_sql","sql":"select 4","parameters":{},"metricId":"personal_instruction_count"}
            """))));
        var action = await provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None);
        var feedback = provider.Feedback(action, new { resultId = "r1" });

        Check.Equal("user", feedback.GetProperty("role").GetString());
        var envelope = JsonDocument.Parse(feedback.GetProperty("content").GetString()!).RootElement;
        Check.Equal("execute_sql", envelope.GetProperty("tool").GetString());
        Check.Equal("r1", envelope.GetProperty("result").GetProperty("resultId").GetString());
    }

    public static async Task RetryAfterHonorsCancellationBudget()
    {
        var chat = 0;
        var handler = Handler((_, _) =>
        {
            chat++;
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(10));
            return response;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await ThrowsCanceled(() => Provider(handler)
            .NextAsync(Messages(), ToolDefinitions.All, cts.Token));

        Check.Equal(1, chat);
    }

    public static void ParseLegacySqlDirectly()
    {
        var json = """
            {"action":"sql","query":"select count(*) n from public.sungero_wf_task","purpose":"Проверка","metricId":"personal_instruction_count"}
            """;
        var action = QwenProvider.ParseAction(json, JsonSerializer.SerializeToElement(new
        {
            role = "assistant",
            content = json
        }, HarnessJson.Options));

        Check.Equal("execute_sql", action.Name);
        Check.Equal("select count(*) n from public.sungero_wf_task", action.Arguments.GetProperty("sql").GetString());
    }

    private static async Task<ModelAction> GigachatAction(string sql)
    {
        var handler = Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK, NativeCall(
                "execute_sql",
                $$"""{"sql":"{{sql}}","parameters":{},"metricId":"personal_instruction_count"}""",
                "state")));
        var tokens = new GigaChatTokenProvider(
            new HttpClient(handler),
            "test-key",
            "GIGACHAT_API_CORP",
            TimeProvider.System);
        var provider = new GigaChatProvider(
            new HttpClient(handler),
            tokens,
            "GigaChat-2",
            new Uri("https://example.test/chat/completions"));
        return await provider.NextAsync(Messages(), ToolDefinitions.All, CancellationToken.None);
    }

    private static QwenProvider Provider(FakeHttpHandler handler) =>
        new(new HttpClient(handler), "test-token", "Qwen/Qwen3.8-27B",
            new Uri("https://example.test/v1/chat/completions"));

    private static FakeHttpHandler Handler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) =>
        new((request, ct) => Task.FromResult(send(request, ct)));

    private static IReadOnlyList<JsonElement> Messages() =>
    [
        JsonSerializer.SerializeToElement(new { role = "user", content = "Сколько поручений?" })
    ];

    private static string Text(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "stop",
                    message = new { role = "assistant", content }
                }
            }
        });

    private static string Token(string value) =>
        JsonSerializer.Serialize(new
        {
            access_token = value,
            expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
        });

    private static string NativeCall(string name, string argumentsJson, string state)
    {
        var arguments = JsonDocument.Parse(argumentsJson).RootElement.Clone();
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "stop",
                    message = new
                    {
                        role = "assistant",
                        content = "",
                        functions_state_id = state,
                        function_call = new { name, arguments }
                    }
                }
            }
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static async Task<HarnessException> ThrowsHarness(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HarnessException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected HarnessException.");
    }

    private static async Task ThrowsCanceled(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Expected OperationCanceledException.");
    }
}
