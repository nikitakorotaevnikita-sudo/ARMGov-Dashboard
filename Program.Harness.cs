#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArmGov.Harness;
using ArmGov.Harness.Hosting;
using Npgsql;

partial class Program
{
    internal static readonly AsyncLocal<HarnessRunSnapshot?> HarnessSnapshotScope = new();
    internal static readonly AsyncLocal<NpgsqlConnection?> HarnessSharedConnection = new();

    public static Func<IModelProvider>? TestModelProviderFactory;
    public static Func<IEmployeeResolver>? TestEmployeeResolverFactory;
    public static Func<IQueryExecutor>? TestQueryExecutorFactory;
    public static Func<IAnalysisRunner>? TestAnalysisRunnerFactory;
    public static Func<bool>? TestAnalyticsEnabled;

    static string EffectiveNoticeNotIn => HarnessSnapshotScope.Value?.NoticeNotIn ?? NoticeNotIn;
    static string EffectiveNoticeList => HarnessSnapshotScope.Value?.NoticeList ?? NoticeList;

    sealed class DashboardConnectionLease : IDisposable
    {
        public NpgsqlConnection Connection { get; }
        private readonly IDisposable? _owned;

        private DashboardConnectionLease(NpgsqlConnection connection, IDisposable? owned)
        {
            Connection = connection;
            _owned = owned;
        }

        public static DashboardConnectionLease Open()
        {
            var shared = HarnessSharedConnection.Value;
            if (shared != null)
                return new DashboardConnectionLease(shared, null);

            var connection = new NpgsqlConnection(Cs);
            connection.Open();
            return new DashboardConnectionLease(connection, connection);
        }

        public void Dispose() => _owned?.Dispose();
    }

    static void HandleAnalysis(HttpListenerContext ctx) =>
        GetAnalysisEndpoint().HandleAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();

    static AnalysisEndpoint GetAnalysisEndpoint() =>
        new(RunAnalysisAsync, () => TestAnalyticsEnabled?.Invoke() ?? Conf.AnalyticsEnabled);

    internal static async Task<AnalysisResponse> RunAnalysisAsync(
        AnalysisRequest request,
        CancellationToken ct)
    {
        var testRunner = TestAnalysisRunnerFactory?.Invoke();
        if (testRunner != null)
            return await testRunner.RunAsync(request, ct).ConfigureAwait(false);

        var snapshot = HarnessRunSnapshot.Capture();
        if (!snapshot.Llm.IsConfigured)
        {
            return new AnalysisResponse(
                Guid.NewGuid().ToString("N"),
                "failed",
                null,
                Array.Empty<StoredResult>(),
                Array.Empty<AgentStep>(),
                0,
                Array.Empty<string>(),
                null,
                new HarnessError(
                    "llm_not_configured",
                    "Модель не настроена: укажите токен LLM в бэк-офисе.",
                    false));
        }

        var runner = CreateAnalysisRunner(snapshot);
        return await runner.RunAsync(request, ct).ConfigureAwait(false);
    }

    static IAnalysisRunner CreateAnalysisRunner(HarnessRunSnapshot snapshot)
    {
        var catalog = AnalyticsCatalog.Load(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
        var provider = TestModelProviderFactory?.Invoke() ?? CreateModelProvider(snapshot.Llm);
        var employees = TestEmployeeResolverFactory?.Invoke()
            ?? new EmployeeResolver(snapshot.ConnectionString);
        var executor = TestQueryExecutorFactory?.Invoke()
            ?? new ReadOnlyExecutor(snapshot.ConnectionString, catalog.AllowedRelations);
        var dispatcher = new ToolDispatcher(
            catalog,
            employees,
            executor,
            (name, args, ct) => DashboardMetricHarnessAsync(name, args, snapshot, ct));
        return new AnalysisAgent(provider, dispatcher, TimeProvider.System);
    }

    static IModelProvider CreateModelProvider(HarnessLlmSnapshot llm)
    {
        if (llm.IsGigaChat)
        {
            var tokenProvider = new GigaChatTokenProvider(
                Http,
                llm.Token,
                string.IsNullOrWhiteSpace(llm.Scope) ? "GIGACHAT_API_CORP" : llm.Scope,
                TimeProvider.System);
            return new GigaChatProvider(
                Http,
                tokenProvider,
                llm.Model,
                new Uri(llm.Url));
        }

        return new QwenProvider(
            Http,
            llm.Token,
            llm.Model,
            new Uri(llm.Url));
    }

    static async Task<QueryResult> DashboardMetricHarnessAsync(
        string name,
        JsonElement args,
        HarnessRunSnapshot snapshot,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(snapshot.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cancelRegistration = ct.Register(() =>
        {
            try { connection.Close(); }
            catch { /* connection already closed */ }
        });

        var previousSnapshot = HarnessSnapshotScope.Value;
        var previousConnection = HarnessSharedConnection.Value;
        HarnessSnapshotScope.Value = snapshot;
        HarnessSharedConnection.Value = connection;
        try
        {
            var payload = ToolCall(name, args);
            return DashboardResultConverter.ToQueryResult(name, payload);
        }
        finally
        {
            HarnessSnapshotScope.Value = previousSnapshot;
            HarnessSharedConnection.Value = previousConnection;
        }
    }

    internal sealed record HarnessRunSnapshot
    {
        public string ConnectionString { get; init; } = "";
        public string NoticeNotIn { get; init; } = "";
        public string NoticeList { get; init; } = "";
        public HarnessLlmSnapshot Llm { get; init; } = new();

        public static HarnessRunSnapshot Capture() => new()
        {
            ConnectionString = Cs,
            NoticeNotIn = Program.NoticeNotIn,
            NoticeList = Program.NoticeList,
            Llm = HarnessLlmSnapshot.Capture()
        };
    }

    internal sealed class HarnessLlmSnapshot
    {
        public string Active { get; init; } = LlmQwenId;
        public string Url { get; init; } = LlmQwenUrl;
        public string Model { get; init; } = LlmQwenModel;
        public string Token { get; init; } = "";
        public string Scope { get; init; } = "";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);

        public bool IsGigaChat =>
            Url.IndexOf("giga.chat", StringComparison.OrdinalIgnoreCase) >= 0
            || Url.IndexOf("gigachat", StringComparison.OrdinalIgnoreCase) >= 0
            || Model.StartsWith("GigaChat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Active, LlmGigaId, StringComparison.OrdinalIgnoreCase);

        public static HarnessLlmSnapshot Capture()
        {
            var preset = FindPreset(Conf.Llm.Active);
            return new HarnessLlmSnapshot
            {
                Active = Conf.Llm.Active ?? LlmQwenId,
                Url = Conf.Llm.Url ?? LlmQwenUrl,
                Model = Conf.Llm.Model ?? LlmQwenModel,
                Token = Conf.Llm.Token ?? "",
                Scope = string.IsNullOrWhiteSpace(Conf.Llm.Scope)
                    ? preset?.Scope ?? ""
                    : Conf.Llm.Scope
            };
        }
    }

    static class DashboardResultConverter
    {
        private static readonly Dictionary<string, string[]> PreferredArrays =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["overview"] = new[] { "processes", "burning", "bottlenecks" },
                ["process"] = new[] { "funnel", "stages", "items" },
                ["leaders"] = new[] { "items" },
                ["leader_tasks"] = new[] { "items", "tasks", "all" },
                ["stuck"] = new[] { "items" },
                ["by_kind"] = new[] { "items" },
                ["departments"] = new[] { "items" },
                ["my_tasks"] = new[] { "all", "items" },
                ["appeal_topics"] = new[] { "topQuestions", "sections", "treemap" },
                ["execution_discipline"] = new[] { "months" }
            };

        public static QueryResult ToQueryResult(string toolName, object payload)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                throw new HarnessException(new HarnessError(
                    "dashboard_metric_failed",
                    errorElement.GetString() ?? "ошибка метрики дашборда",
                    true));
            }

            var array = FindArray(toolName, root);
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            {
                return new QueryResult(
                    "dashboard:" + toolName,
                    Array.Empty<ColumnSpec>(),
                    Array.Empty<JsonElement[]>(),
                    null,
                    DateTimeOffset.UtcNow,
                    new Truncation(false, false, Array.Empty<string>(), false),
                    new[] { "метрика не вернула табличных строк" });
            }

            var first = array[0];
            if (first.ValueKind != JsonValueKind.Object)
            {
                throw new HarnessException(new HarnessError(
                    "dashboard_metric_shape",
                    "Результат метрики не содержит объектных строк.",
                    false));
            }

            var columns = first.EnumerateObject()
                .Select(property => new ColumnSpec(
                    property.Name,
                    property.Name,
                    ColumnType(property.Value)))
                .ToArray();

            var rows = new List<JsonElement[]>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var row = columns
                    .Select(column => item.TryGetProperty(column.Name, out var value)
                        ? value.Clone()
                        : JsonSerializer.SerializeToElement<object?>(null))
                    .ToArray();
                rows.Add(row);
            }

            return ResultLimiter.Limit(new QueryResult(
                "dashboard:" + toolName,
                columns,
                rows.ToArray(),
                null,
                DateTimeOffset.UtcNow,
                new Truncation(false, false, Array.Empty<string>(), false),
                Array.Empty<string>()));
        }

        private static JsonElement FindArray(string toolName, JsonElement root)
        {
            if (PreferredArrays.TryGetValue(toolName, out var names))
            {
                foreach (var name in names)
                {
                    if (root.TryGetProperty(name, out var candidate) &&
                        candidate.ValueKind == JsonValueKind.Array)
                        return candidate;
                }
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.GetArrayLength() > 0 &&
                    property.Value[0].ValueKind == JsonValueKind.Object)
                    return property.Value;
            }

            return default;
        }

        private static string ColumnType(JsonElement value) =>
            value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Number => "number",
                JsonValueKind.String when LooksLikeDate(value.GetString()) => "date",
                _ => "string"
            };

        private static bool LooksLikeDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 10)
                return false;
            return char.IsDigit(text[0]) && text[4] == '-' && text[7] == '-'
                && DateTime.TryParse(text, out _);
        }
    }
}
