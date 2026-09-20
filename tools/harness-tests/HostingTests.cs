#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using ArmGov.Harness;
using ArmGov.Harness.Hosting;

public static class HostingTests
{
    public static async Task HostUsesInjectedAnalysisRunner()
    {
        var runner = new RecordingAnalysisRunner();
        HarnessHost.TestAnalysisRunnerFactory = () => runner;
        try
        {
            var response = await HarnessHost.RunAnalysisAsync(
                new AnalysisRequest("Покажи просрочку", []),
                CancellationToken.None);

            Check.Equal("runner-test", response.RunId);
            Check.Equal(1, runner.CallCount);
        }
        finally
        {
            HarnessHost.TestAnalysisRunnerFactory = null;
        }
    }

    public static void AnalysisResponseSerializationKeepsHttpContract()
    {
        var response = new AnalysisResponse(
            "contract-test",
            "completed",
            null,
            Array.Empty<StoredResult>(),
            Array.Empty<AgentStep>(),
            0,
            Array.Empty<string>(),
            null,
            null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, HarnessJson.Options));
        var keys = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var expected = new[]
        {
            "runId", "status", "report", "datasets", "steps", "elapsedMs", "warnings",
            "clarification", "error"
        };

        Check.Equal(expected.Length, keys.Count);
        foreach (var key in expected)
            Check.True(keys.Contains(key));
    }

    public static void LocalHostNamesAreAccepted()
    {
        Check.True(LocalAccess.IsLocalHostName("localhost:5080"));
        Check.True(LocalAccess.IsLocalHostName("127.0.0.1:5080"));
        Check.True(!LocalAccess.IsLocalHostName("evil.example.com:5080"));
    }

    public static void SameOriginAllowsMatchingHost()
    {
        Check.True(LocalAccess.IsSameOrigin("http://localhost:5080", "localhost:5080"));
        Check.True(!LocalAccess.IsSameOrigin("http://evil.example.com:5080", "localhost:5080"));
    }

    public static void NonLocalHostHeaderIsRejected()
    {
        Check.True(!AnalysisEndpoint.IsLocalRequest(
            true,
            IPAddress.Loopback,
            "evil.example.com:5080"));
    }

    public static async Task GetMethodReturns405()
    {
        var (status, _) = await InvokeAsync(HttpMethod.Get.Method, "{}");
        Check.Equal(405, status);
    }

    public static async Task NonLocalHostHeaderReturns403()
    {
        var (status, _) = await InvokeAsync(
            HttpMethod.Post.Method,
            """{"question":"Сравни","selections":[]}""",
            hostName: "evil.example.com:5080");
        Check.Equal(403, status);
    }

    public static async Task OversizedBodyReturns413()
    {
        var huge = "{\"question\":\"" + new string('x', AnalysisEndpoint.MaxBodyBytes) + "\",\"selections\":[]}";
        var (status, _) = await InvokeAsync(HttpMethod.Post.Method, huge);
        Check.Equal(413, status);
    }

    public static async Task MalformedJsonReturns400()
    {
        var (status, _) = await InvokeAsync(HttpMethod.Post.Method, "{question:");
        Check.Equal(400, status);
    }

    public static async Task InvalidSelectionReturns400()
    {
        var (status, _) = await InvokeAsync(
            HttpMethod.Post.Method,
            """{"question":"Сравни","selections":[{"mention":"","employeeId":0}]}""");
        Check.Equal(400, status);
    }

    public static async Task UnknownJsonFieldReturns400()
    {
        var (status, _) = await InvokeAsync(
            HttpMethod.Post.Method,
            """{"question":"Сравни","selections":[],"extra":1}""");
        Check.Equal(400, status);
    }

    public static async Task WrongContentTypeReturns400()
    {
        var (status, _) = await InvokeAsync(
            HttpMethod.Post.Method,
            """{"question":"Сравни","selections":[]}""",
            contentType: "text/plain");
        Check.Equal(400, status);
    }

    public static async Task EmptyBodyDoesNotCallModel()
    {
        var (status, _) = await InvokeAsync(
            HttpMethod.Post.Method,
            "",
            provider: new CountingProvider());
        Check.Equal(400, status);
        Check.Equal(0, CountingProvider.Calls);
    }

    public static async Task PostReturnsCamelCaseAnalysisResponse()
    {
        var (status, body) = await InvokeAsync(
            HttpMethod.Post.Method,
            """{"question":"Сравни","selections":[]}""",
            configureHooks: () =>
            {
                HarnessHost.TestModelProviderFactory = () => CreateClarifyProvider();
                HarnessHost.TestEmployeeResolverFactory = () => new FakeEmployeeResolver(
                    new[] { new EmployeeCandidate(101, "Иванов", "Отдел") });
                HarnessHost.TestQueryExecutorFactory = () =>
                    new FakeQueryExecutor(Array.Empty<JsonElement[]>());
            });
        Check.Equal(200, status);
        Check.True(body.TryGetProperty("runId", out _));
        Check.True(body.TryGetProperty("steps", out _));
        var responseStatus = body.GetProperty("status").GetString();
        Check.True(responseStatus is "completed" or "needs_clarification" or "no_data" or "incomplete" or "failed");
    }

    public static async Task MissingLlmConfigReturnsFailedWithoutModel()
    {
        var previousToken = HarnessHost.LlmToken;
        try
        {
            var (status, body) = await InvokeAsync(
                HttpMethod.Post.Method,
                """{"question":"Сравни","selections":[]}""",
                configureHooks: () =>
                {
                    HarnessHost.TestModelProviderFactory = () => new CountingProvider();
                    HarnessHost.LlmToken = "";
                });
            Check.Equal(200, status);
            Check.Equal("failed", body.GetProperty("status").GetString());
            Check.Equal(0, CountingProvider.Calls);
        }
        finally
        {
            HarnessHost.LlmToken = previousToken;
        }
    }

    public static void ConfigJsonIsNotStaticAllowlistMember()
    {
        Check.True(!HarnessHost.StaticAllowlist.Contains("config.json"));
    }

    public static async Task DisabledAnalyticsReturns404()
    {
        var (status, _) = await InvokeAsync(
            HttpMethod.Post.Method,
            """{"question":"Сравни","selections":[]}""",
            configureHooks: () => HarnessHost.TestAnalyticsEnabled = () => false);
        Check.Equal(404, status);
    }

    public static void SnapshotCapturesModelAtRunStart()
    {
        var previous = HarnessHost.LlmModel;
        HarnessHost.LlmModel = "Snapshot-A";
        var snapshotModel = HarnessHost.CaptureLlmModel();
        HarnessHost.LlmModel = "Snapshot-B";
        Check.Equal("Snapshot-A", snapshotModel);
        HarnessHost.LlmModel = previous;
    }

    private static ScriptedProvider CreateClarifyProvider() =>
        new(
            Action("find_employees", Json("""{"tokens":["Иванов"]}""")),
            Action("clarify", Json("""{"question":"Кого выбрать?","candidateIds":[101]}""")));

    private static async Task<(int Status, JsonElement Body)> InvokeAsync(
        string method,
        string body,
        string hostName = "localhost:5080",
        string contentType = "application/json",
        IModelProvider? provider = null,
        Action? configureHooks = null)
    {
        ResetHarnessHooks();
        configureHooks?.Invoke();
        if (provider != null)
            HarnessHost.TestModelProviderFactory = () => provider;

        var endpoint = new AnalysisEndpoint(
            HarnessHost.RunAnalysisAsync,
            () => HarnessHost.TestAnalyticsEnabled?.Invoke() ?? HarnessHost.AnalyticsEnabled);

        var bytes = Encoding.UTF8.GetBytes(body);
        int statusCode = 0;
        string json = "{}";
        await endpoint.HandleRequestAsync(
            method,
            true,
            IPAddress.Loopback,
            hostName,
            $"http://{hostName}",
            contentType,
            bytes.LongLength,
            new MemoryStream(bytes),
            async (status, payload) =>
            {
                statusCode = status;
                json = JsonSerializer.Serialize(payload, HarnessJson.Options);
                await Task.CompletedTask;
            },
            CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        return (statusCode, document.RootElement.Clone());
    }

    private static void ResetHarnessHooks()
    {
        HarnessHost.TestModelProviderFactory = null;
        HarnessHost.TestEmployeeResolverFactory = null;
        HarnessHost.TestQueryExecutorFactory = null;
        HarnessHost.TestAnalysisRunnerFactory = null;
        HarnessHost.TestAnalyticsEnabled = null;
        CountingProvider.Calls = 0;
    }

    private static ModelAction Action(string name, JsonElement args) =>
        new(name, args, Json("""{"role":"assistant","content":"call"}"""));

    private static JsonElement Json(string raw) =>
        JsonDocument.Parse(raw).RootElement.Clone();

    private sealed class CountingProvider : IModelProvider
    {
        public static int Calls;
        public Task<ModelAction> NextAsync(
            IReadOnlyList<JsonElement> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken ct)
        {
            Calls++;
            throw new HarnessException(new HarnessError("unexpected_model_call", "model was invoked", false));
        }

        public JsonElement Feedback(ModelAction action, object result) =>
            Json("""{"role":"user","content":"{}"}""");
    }

    private sealed class RecordingAnalysisRunner : IAnalysisRunner
    {
        public int CallCount { get; private set; }

        public Task<AnalysisResponse> RunAsync(AnalysisRequest request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new AnalysisResponse(
                "runner-test",
                "completed",
                null,
                Array.Empty<StoredResult>(),
                Array.Empty<AgentStep>(),
                0,
                Array.Empty<string>(),
                null,
                null));
        }
    }

    private sealed class FakeEmployeeResolver : IEmployeeResolver
    {
        private readonly EmployeeCandidate[] _search;
        public FakeEmployeeResolver(EmployeeCandidate[] search) => _search = search;
        public Task<EmployeeCandidate[]> SearchAsync(string[] tokens, CancellationToken ct) =>
            Task.FromResult(_search);
        public Task<EmployeeCandidate?> GetAsync(long id, CancellationToken ct) =>
            Task.FromResult(_search.FirstOrDefault(candidate => candidate.Id == id));
    }

    private sealed class FakeQueryExecutor : IQueryExecutor
    {
        private readonly JsonElement[][] _rows;
        public FakeQueryExecutor(JsonElement[][] rows) => _rows = rows;
        public Task<QueryResult> ExecuteAsync(QuerySpec query, CancellationToken ct) =>
            Task.FromResult(new QueryResult(
                "sql",
                Array.Empty<ColumnSpec>(),
                _rows,
                query.Sql,
                DateTimeOffset.UtcNow,
                new Truncation(false, false, Array.Empty<string>(), false),
                Array.Empty<string>()));
    }
}
