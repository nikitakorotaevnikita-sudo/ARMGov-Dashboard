#nullable enable

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArmGov.Harness;

public static class QwenJsonClientTests
{
    public static async Task SendsArioRequestWithConfiguredModelAndThinkingDisabled()
    {
        var handler = new CaptureHandler((_, _, _) => Task.FromResult(Reply("{\"value\":\"ok\"}")));
        var client = Client(handler);

        var value = await client.CompleteAsync<JsonReply>(
            "Return one JSON object.", new { requestId = "r1" }, 321, CancellationToken.None);

        Check.Equal("ok", value.Value);
        var request = handler.Requests.Single();
        Check.Equal("POST", request.Method);
        Check.Equal("https://example.test/v1/chat/completions", request.Uri);
        Check.Equal("Bearer", request.AuthorizationScheme);
        Check.Equal("test-token", request.AuthorizationParameter);
        using var body = JsonDocument.Parse(request.Body);
        Check.Equal("Qwen/Qwen3.8-27B", body.RootElement.GetProperty("model").GetString());
        Check.Equal(0.2, body.RootElement.GetProperty("temperature").GetDouble());
        Check.Equal(false, body.RootElement.GetProperty("chat_template_kwargs")
            .GetProperty("enable_thinking").GetBoolean());
        var messages = body.RootElement.GetProperty("messages");
        Check.Equal(2, messages.GetArrayLength());
        Check.Equal("system", messages[0].GetProperty("role").GetString());
        Check.Equal("Return one JSON object.", messages[0].GetProperty("content").GetString());
        Check.Equal("user", messages[1].GetProperty("role").GetString());
        Check.Equal("{\"requestId\":\"r1\"}", messages[1].GetProperty("content").GetString());
        Check.Equal(321, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    public static async Task DeserializesOnePlainJsonObject()
    {
        var value = await Client(RespondingWith("{\"value\":\"plain\"}"))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None);

        Check.Equal("plain", value.Value);
    }

    public static async Task DeserializesOneFencedJsonObject()
    {
        var value = await Client(RespondingWith("```json\n{\"value\":\"fenced\"}\n```"))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None);

        Check.Equal("fenced", value.Value);
    }

    public static async Task PreservesBracesInsideJsonStrings()
    {
        var value = await Client(RespondingWith("{\"value\":\"{literal} and \\\"quoted\\\"\"}"))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None);

        Check.Equal("{literal} and \"quoted\"", value.Value);
    }

    public static async Task UsesReasoningContentWhenContentIsNull()
    {
        var handler = new CaptureHandler((_, _, _) => Task.FromResult(
            Reply(null, "{\"value\":\"reasoning\"}")));

        var value = await Client(handler)
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None);

        Check.Equal("reasoning", value.Value);
    }

    public static async Task RetriesOneRateLimitedRequestAfterRetryAfter()
    {
        var calls = 0;
        var handler = new CaptureHandler((_, _, _) =>
        {
            calls++;
            if (calls == 1)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("slow down", Encoding.UTF8, "text/plain")
                };
                rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(rateLimited);
            }

            return Task.FromResult(Reply("{\"value\":\"retried\"}"));
        });

        var value = await Client(handler)
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None);

        Check.Equal("retried", value.Value);
        Check.Equal(2, calls);
        Check.Equal(2, handler.Requests.Count);
    }

    public static async Task ReportsSanitizedBoundedHttpErrors()
    {
        var detail = "server\r\n" + new string('x', 400);
        var error = await ThrowsHarness(() => Client(new CaptureHandler((_, _, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(detail, Encoding.UTF8, "text/plain")
                })))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None));

        Check.Equal("provider_http_error", error.Error.Code);
        Check.Equal(true, error.Error.Retryable);
        Check.True(error.Error.Message.Contains("500", StringComparison.Ordinal));
        Check.True(error.Error.Message.Length <= 360);
        Check.True(!error.Error.Message.Contains('\r'));
        Check.True(!error.Error.Message.Contains('\n'));
        Check.True(!error.Error.Message.Contains("test-token", StringComparison.Ordinal));
    }

    public static async Task RejectsLengthFinishedResponses()
    {
        var error = await ThrowsHarness(() => Client(RespondingWith("{\"value\":\"partial\"}", "length"))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None));

        Check.Equal("truncated_response", error.Error.Code);
    }

    public static async Task HonorsCancellation()
    {
        var handler = new CaptureHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Reply("{\"value\":\"never\"}");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await ThrowsCanceled(() => Client(handler)
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, cancellation.Token));
    }

    public static async Task RejectsTwoTopLevelJsonObjects()
    {
        var error = await ThrowsHarness(() => Client(RespondingWith("{\"value\":\"first\"}{\"value\":\"second\"}"))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None));

        Check.Equal("provider_protocol_error", error.Error.Code);
    }

    public static async Task RejectsProseOutsideJsonObject()
    {
        var error = await ThrowsHarness(() => Client(RespondingWith("Here is the result: {\"value\":\"no\"}"))
            .CompleteAsync<JsonReply>("system", new { id = 1 }, 10, CancellationToken.None));

        Check.Equal("provider_protocol_error", error.Error.Code);
    }

    private static QwenJsonClient Client(CaptureHandler handler) =>
        new(new HttpClient(handler), "test-token", "Qwen/Qwen3.8-27B",
            new Uri("https://example.test/v1/chat/completions"));

    private static CaptureHandler RespondingWith(string content, string finishReason = "stop") =>
        new((_, _, _) => Task.FromResult(Reply(content, finishReason: finishReason)));

    private static HttpResponseMessage Reply(
        string? content,
        string? reasoningContent = null,
        string finishReason = "stop") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        finish_reason = finishReason,
                        message = new
                        {
                            role = "assistant",
                            content,
                            reasoning_content = reasoningContent
                        }
                    }
                }
            }), Encoding.UTF8, "application/json")
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

    private sealed record JsonReply(string Value);

    private sealed record CapturedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<int, CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _send;

        public CaptureHandler(Func<int, CapturedRequest, CancellationToken, Task<HttpResponseMessage>> send) =>
            _send = send;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var snapshot = new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(snapshot);
            return await _send(Requests.Count, snapshot, cancellationToken);
        }
    }
}
