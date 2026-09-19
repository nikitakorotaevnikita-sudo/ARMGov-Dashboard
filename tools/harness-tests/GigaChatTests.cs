#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using ArmGov.Harness;

public static class GigaChatTests
{
    public static async Task EmptyContentFunctionCallAndStateRoundTrip()
    {
        var handler = Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK, NativeCall("execute_sql", """{"sql":"select 1"}""", "state-1")));
        var provider = Provider(handler);

        var action = await provider.NextAsync(Messages(), Tools(), CancellationToken.None);

        Check.Equal("execute_sql", action.Name);
        Check.Equal("state-1", action.AssistantMessage.GetProperty("functions_state_id").GetString());
        var feedback = provider.Feedback(action, new { resultId = "r1" });
        Check.Equal("function", feedback.GetProperty("role").GetString());
        Check.Equal("execute_sql", feedback.GetProperty("name").GetString());
        Check.Equal("state-1", feedback.GetProperty("functions_state_id").GetString());
        Check.Equal("r1", JsonDocument.Parse(feedback.GetProperty("content").GetString()!)
            .RootElement.GetProperty("resultId").GetString());

        var chatBody = JsonDocument.Parse(handler.RequestBodies.Single(body => body.Contains("\"functions\"")));
        Check.Equal("auto", chatBody.RootElement.GetProperty("function_call").GetString());
        Check.Equal("execute_sql", chatBody.RootElement.GetProperty("functions")[0]
            .GetProperty("name").GetString());
        Check.Equal("state-old", chatBody.RootElement.GetProperty("messages")[1]
            .GetProperty("functions_state_id").GetString());
    }

    public static async Task PlainTextIsUnstructuredResponse()
    {
        var provider = Provider(Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK, TextOnly("12 и 8"))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), Tools(), CancellationToken.None));

        Check.Equal("unstructured_response", error.Error.Code);
    }

    public static async Task MalformedArgumentsAreRejected()
    {
        var provider = Provider(Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK, NativeCall("execute_sql", "\"select 1\"", "state"))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), Tools(), CancellationToken.None));

        Check.Equal("invalid_function_arguments", error.Error.Code);
    }

    public static async Task ExtraArgumentsAreRejected()
    {
        var provider = Provider(Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK,
                NativeCall("execute_sql", """{"sql":"select 1","password":"secret"}""", "state"))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), Tools(), CancellationToken.None));

        Check.Equal("invalid_function_arguments", error.Error.Code);
    }

    public static async Task UnknownFunctionIsRejected()
    {
        var provider = Provider(Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK, NativeCall("drop_database", "{}", "state"))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), Tools(), CancellationToken.None));

        Check.Equal("unknown_function", error.Error.Code);
    }

    public static async Task LengthFinishReasonIsRejected()
    {
        var provider = Provider(Handler((request, _) => request.RequestUri!.AbsolutePath.Contains("oauth")
            ? Json(HttpStatusCode.OK, Token("test-only"))
            : Json(HttpStatusCode.OK,
                NativeCall("execute_sql", """{"sql":"select 1"}""", "state", "length"))));

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), Tools(), CancellationToken.None));

        Check.Equal("truncated_response", error.Error.Code);
    }

    public static async Task UnauthorizedInvalidatesTokenAndRetriesOnce()
    {
        var oauth = 0;
        var chat = 0;
        var handler = Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("oauth"))
                return Json(HttpStatusCode.OK, Token("token-" + ++oauth));
            chat++;
            Check.Equal("Bearer token-" + chat, request.Headers.Authorization!.ToString());
            return chat == 1
                ? Json(HttpStatusCode.Unauthorized, """{"message":"expired"}""")
                : Json(HttpStatusCode.OK, NativeCall("execute_sql", """{"sql":"select 1"}""", "state"));
        });

        var action = await Provider(handler).NextAsync(Messages(), Tools(), CancellationToken.None);

        Check.Equal("execute_sql", action.Name);
        Check.Equal(2, oauth);
        Check.Equal(2, chat);
    }

    public static async Task RepeatedUnauthorizedStopsAfterOneRetry()
    {
        var oauth = 0;
        var chat = 0;
        const string secret = "do-not-leak";
        var provider = Provider(Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("oauth"))
            {
                oauth++;
                return Json(HttpStatusCode.OK, Token("access"));
            }
            chat++;
            return Json(HttpStatusCode.Unauthorized, """{"message":"do-not-leak"}""");
        }), secret);

        var error = await ThrowsHarness(() =>
            provider.NextAsync(Messages(), Tools(), CancellationToken.None));

        Check.Equal("provider_http_error", error.Error.Code);
        Check.Equal(2, oauth);
        Check.Equal(2, chat);
        Check.True(!error.Message.Contains(secret, StringComparison.Ordinal));
    }

    public static async Task OAuthCancellationIsObserved()
    {
        var handler = Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(HttpStatusCode.OK, Token("never"));
        });
        var tokens = Tokens(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await ThrowsCanceled(() => tokens.GetAsync(cts.Token));
    }

    public static async Task WaitingForTokenLockIsCancelable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Handler(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Json(HttpStatusCode.OK, Token("access"));
        });
        var tokens = Tokens(handler);
        var first = tokens.GetAsync(CancellationToken.None);
        await entered.Task;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await ThrowsCanceled(() => tokens.GetAsync(cts.Token));

        release.TrySetResult();
        Check.Equal("access", await first);
    }

    public static async Task ExpiredTokenIsRefreshed()
    {
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
        var calls = 0;
        var handler = Handler((_, _) =>
            Json(HttpStatusCode.OK, Token("token-" + ++calls, time.GetUtcNow().AddMinutes(2))));
        var tokens = Tokens(handler, time: time);

        Check.Equal("token-1", await tokens.GetAsync(CancellationToken.None));
        Check.Equal("token-1", await tokens.GetAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(61));
        Check.Equal("token-2", await tokens.GetAsync(CancellationToken.None));
        Check.Equal(2, calls);
    }

    public static async Task RetryAfterHonorsCancellationBudget()
    {
        var chat = 0;
        var handler = Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("oauth"))
                return Json(HttpStatusCode.OK, Token("access"));
            chat++;
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(10));
            return response;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await ThrowsCanceled(() => Provider(handler)
            .NextAsync(Messages(), Tools(), cts.Token));

        Check.Equal(1, chat);
    }

    private static GigaChatProvider Provider(FakeHttpHandler handler, string key = "test-key") =>
        new(new HttpClient(handler), Tokens(handler, key), "GigaChat-2",
            new Uri("https://example.test/chat/completions"));

    private static GigaChatTokenProvider Tokens(
        FakeHttpHandler handler,
        string key = "test-key",
        TimeProvider? time = null) =>
        new(new HttpClient(handler), key, "GIGACHAT_API_CORP", time ?? TimeProvider.System);

    private static FakeHttpHandler Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(send);

    private static FakeHttpHandler Handler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) =>
        new((request, ct) => Task.FromResult(send(request, ct)));

    private static IReadOnlyList<JsonElement> Messages() =>
    [
        JsonSerializer.SerializeToElement(new { role = "user", content = "Вопрос" }),
        JsonSerializer.SerializeToElement(new
        {
            role = "assistant",
            content = "",
            functions_state_id = "state-old",
            function_call = new { name = "execute_sql", arguments = new { sql = "select 0" } }
        })
    ];

    private static IReadOnlyList<ToolDefinition> Tools() =>
    [
        new ToolDefinition(
            "execute_sql",
            "Выполнить SELECT",
            JsonDocument.Parse("""
                {
                  "type":"object",
                  "properties":{"sql":{"type":"string"}},
                  "required":["sql"],
                  "additionalProperties":false
                }
                """).RootElement.Clone())
    ];

    private static string Token(string value, DateTimeOffset? expires = null) =>
        JsonSerializer.Serialize(new
        {
            access_token = value,
            expires_at = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeMilliseconds()
        });

    private static string NativeCall(
        string name,
        string argumentsJson,
        string state,
        string finishReason = "stop")
    {
        var arguments = JsonDocument.Parse(argumentsJson).RootElement.Clone();
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = finishReason,
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

    private static string TextOnly(string text) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "stop",
                    message = new { role = "assistant", content = text }
                }
            }
        });

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
