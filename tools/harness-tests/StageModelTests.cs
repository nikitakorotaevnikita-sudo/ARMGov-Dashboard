#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArmGov.Harness;

public static class StageModelTests
{
    public static async Task PlanningPassesQuestionTimeAndMetricSummariesWithoutCredentials()
    {
        var client = new RecordingQwenJsonClient(Plan());
        var model = new QwenAnalysisStageModel(client);
        var asOf = DateTimeOffset.Parse("2026-09-20T12:00:00+04:00");

        await model.PlanAsync(new PlanningInput(
            "Покажи дисциплину исполнения",
            asOf,
            [new EntitySelection("Иванов", 42)],
            [new MetricSummary("execution_discipline", "Дисциплина", "execution_discipline"),
             new MetricSummary("generic_query", "Произвольный SELECT-анализ", null)],
            [new PlanningRelationSummary("public.sungero_wf_task", "Поручения", "Карточки поручений")]),
            CancellationToken.None);

        Check.Equal(800, client.MaxTokens);
        Check.True(client.SystemPrompt.Contains("AnalysisPlan", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("JSON object", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("Markdown", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("\"kind\":\"all\"|\"months\"|\"range\"", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("\"route\":\"dashboardMetric\"|\"generatedSql\"", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("metrics[].metricId", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("metrics[].dashboardMetric", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("relations[].name", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("employeeMentions and relationHints are always JSON arrays", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains(
            "{\"metricId\":\"generic_query\",\"period\":{\"kind\":\"months\",\"months\":12,\"from\":null,\"to\":null},\"route\":\"generatedSql\",\"dashboardMetric\":null,\"employeeMentions\":[],\"relationHints\":[\"public.sungero_wf_task\"]}",
            StringComparison.Ordinal));
        var captured = (PlanningInput)client.Input!;
        Check.Equal("Покажи дисциплину исполнения", captured.Question);
        Check.Equal(asOf, captured.AsOf);
        Check.Equal("execution_discipline", captured.Metrics[0].MetricId);
        var json = JsonSerializer.Serialize(client.Input, HarnessJson.Options);
        Check.True(!json.Contains("connection", StringComparison.OrdinalIgnoreCase));
        Check.True(!json.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task SqlGenerationPassesValidatedRelationsAndRequiredDefinitions()
    {
        var client = new RecordingQwenJsonClient(new SqlDraft("select 1", "generic_query"));
        var model = new QwenAnalysisStageModel(client);
        var input = SqlInput();

        await model.DraftSqlAsync(input, CancellationToken.None);

        Check.Equal(1400, client.MaxTokens);
        Check.True(client.SystemPrompt.Contains("@from", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("@to", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("@selected_employee_N", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(client.Input, HarnessJson.Options);
        Check.True(json.Contains("public.sungero_wf_task", StringComparison.Ordinal));
        Check.True(!json.Contains("private.payroll", StringComparison.Ordinal));
        Check.True(json.Contains("generic_query", StringComparison.Ordinal));
        Check.True(json.Contains("assignment.task", StringComparison.Ordinal));
    }

    public static async Task SqlGenerationExcludesRelationDefinitionsOutsidePlanHints()
    {
        var client = new RecordingQwenJsonClient(new SqlDraft("select 1", "generic_query"));
        var model = new QwenAnalysisStageModel(client);
        var baseInput = SqlInput();
        var input = baseInput with
        {
            Catalog = baseInput.Catalog with
            {
                Relations = baseInput.Catalog.Relations.Append(new CatalogRelationProjection(
                    "private.payroll", "Forbidden", [])).ToArray(),
                Relationships = baseInput.Catalog.Relationships.Append(
                    new CatalogRelationshipProjection(
                        "private.payroll.employee_id",
                        "public.sungero_wf_task.id",
                        "many-to-one",
                        "N:1")).ToArray()
            }
        };

        await model.DraftSqlAsync(input, CancellationToken.None);

        var captured = (SqlGenerationInput)client.Input!;
        Check.Equal(2, captured.Catalog.Relations.Length);
        Check.Equal(1, captured.Catalog.Relationships.Length);
        Check.Equal("public.sungero_wf_assignment.task", captured.Catalog.Relationships[0].From);
    }

    public static async Task SqlRepairPassesRejectedSqlAndStructuredValidationErrors()
    {
        var client = new RecordingQwenJsonClient(new SqlDraft("select 1", "generic_query"));
        var model = new QwenAnalysisStageModel(client);
        var input = new SqlRepairInput(
            SqlInput(),
            new SqlDraft("select bad", "generic_query"),
            [new HarnessError("missing_period_binding", "Use @from and @to.", false)]);

        await model.RepairSqlAsync(input, CancellationToken.None);

        Check.Equal(1400, client.MaxTokens);
        var json = JsonSerializer.Serialize(client.Input, HarnessJson.Options);
        Check.True(json.Contains("select bad", StringComparison.Ordinal));
        Check.True(json.Contains("missing_period_binding", StringComparison.Ordinal));
        Check.True(json.Contains("Use @from and @to.", StringComparison.Ordinal));
    }

    public static async Task ReportGenerationPassesBoundedResultManifest()
    {
        var client = new RecordingQwenJsonClient(ReportDraft());
        var model = new QwenAnalysisStageModel(client);
        var input = ReportInput();

        await model.DraftReportAsync(input, CancellationToken.None);

        Check.Equal(1600, client.MaxTokens);
        Check.True(client.SystemPrompt.Contains("{{factId}}", StringComparison.Ordinal));
        Check.True(client.SystemPrompt.Contains("literal numbers", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(client.Input, HarnessJson.Options);
        Check.True(json.Contains("r7", StringComparison.Ordinal));
        Check.True(json.Contains("employee", StringComparison.Ordinal));
        Check.True(json.Contains("number", StringComparison.Ordinal));
        Check.True(json.Contains("rowCount", StringComparison.Ordinal));
        Check.True(json.Contains("truncation", StringComparison.Ordinal));
        Check.True(json.Contains("Ada", StringComparison.Ordinal));
    }

    public static async Task ReportRepairPassesRejectedReportAndValidatorErrors()
    {
        var client = new RecordingQwenJsonClient(ReportDraft());
        var model = new QwenAnalysisStageModel(client);
        var rejected = new ReportDraft(new ReportSpec(
            "Bad 99", Interpretation(), [], [], [], null));
        var input = new ReportRepairInput(
            ReportInput(),
            rejected,
            [new HarnessError("unverified_numeric_text", "Numbers need facts.", false)]);

        await model.RepairReportAsync(input, CancellationToken.None);

        Check.Equal(1600, client.MaxTokens);
        var json = JsonSerializer.Serialize(client.Input, HarnessJson.Options);
        Check.True(json.Contains("Bad 99", StringComparison.Ordinal));
        Check.True(json.Contains("unverified_numeric_text", StringComparison.Ordinal));
        Check.True(json.Contains("Numbers need facts.", StringComparison.Ordinal));
    }

    public static async Task InvalidPlanIsRejectedImmediately()
    {
        var client = new RecordingQwenJsonClient(new AnalysisPlan(
            "generic_query",
            new PeriodSpec("months", 0, null, null),
            AnalysisDataRoute.GeneratedSql,
            null,
            [],
            []));
        var model = new QwenAnalysisStageModel(client);

        var error = await ThrowsHarness(() => model.PlanAsync(new PlanningInput(
            "Question",
            DateTimeOffset.Parse("2026-09-20T12:00:00+04:00"),
            [],
            [new MetricSummary("generic_query", "Query", null)],
            []), CancellationToken.None));

        Check.Equal("invalid_period", error.Error.Code);
    }

    public static async Task UnknownMetricIdIsRejectedImmediately()
    {
        var client = new RecordingQwenJsonClient(new AnalysisPlan(
            "invented_metric",
            new PeriodSpec("months", 12, null, null),
            AnalysisDataRoute.GeneratedSql,
            null,
            [],
            ["public.sungero_wf_task"]));
        var model = new QwenAnalysisStageModel(client);

        var error = await ThrowsHarness(() => model.PlanAsync(PlanningInputWithAllowlists(), CancellationToken.None));

        Check.Equal("invalid_metric_id", error.Error.Code);
    }

    public static async Task UnknownRelationHintIsRejectedImmediately()
    {
        var client = new RecordingQwenJsonClient(new AnalysisPlan(
            "generic_query",
            new PeriodSpec("months", 12, null, null),
            AnalysisDataRoute.GeneratedSql,
            null,
            [],
            ["public.not_in_catalog"]));
        var model = new QwenAnalysisStageModel(client);

        var error = await ThrowsHarness(() => model.PlanAsync(PlanningInputWithAllowlists(), CancellationToken.None));

        Check.Equal("invalid_relation_hint", error.Error.Code);
    }

    public static async Task InvalidSqlDraftIsRejectedImmediately()
    {
        var client = new RecordingQwenJsonClient(new SqlDraft(" ", "generic_query"));
        var model = new QwenAnalysisStageModel(client);

        var error = await ThrowsHarness(() => model.DraftSqlAsync(SqlInput(), CancellationToken.None));

        Check.Equal("invalid_sql", error.Error.Code);
    }

    private static PlanningInput PlanningInputWithAllowlists() => new(
        "Question",
        DateTimeOffset.Parse("2026-09-20T12:00:00+04:00"),
        [],
        [new MetricSummary("generic_query", "Query", null),
         new MetricSummary("execution_discipline", "Discipline", "execution_discipline")],
        [new PlanningRelationSummary("public.sungero_wf_task", "Tasks", "Task cards")]);

    private static SqlGenerationInput SqlInput() => new(
        "Покажи задачи",
        Plan(["public.sungero_wf_task", "public.sungero_wf_assignment"]),
        Interpretation(),
        [new VerifiedEmployee(42, "Иванов", "selected_employee_1")],
        new CatalogProjection(
            [new CatalogRelationProjection(
                "public.sungero_wf_task",
                "Tasks",
                [new CatalogFieldProjection("id", "bigint", "Task id")]),
             new CatalogRelationProjection(
                "public.sungero_wf_assignment",
                "Assignments",
                [new CatalogFieldProjection("task", "bigint", "Task id")])],
            [new CatalogRelationshipProjection(
                "public.sungero_wf_assignment.task",
                "public.sungero_wf_task.id",
                "many-to-one",
                "N:1")],
            new MetricDefinition("generic_query", "items", "created", "Query definition")));

    private static ReportGenerationInput ReportInput() => new(
        "Покажи задачи",
        Interpretation(),
        [new ResultManifest(
            "r7",
            [new ColumnSpec("employee", "Employee", "string"),
             new ColumnSpec("count", "Count", "number")],
            [[JsonSerializer.SerializeToElement("Ada"), JsonSerializer.SerializeToElement(3)]],
            10,
            new Truncation(true, false, [], false))]);

    private static AnalysisPlan Plan(string[]? relationHints = null) => new(
        "generic_query",
        new PeriodSpec("months", 12, null, null),
        AnalysisDataRoute.GeneratedSql,
        null,
        [],
        ImmutableArray.CreateRange(relationHints ?? ["public.sungero_wf_task"]));

    private static Interpretation Interpretation() => new(
        "generic_query", "Tasks", "items", null, null, "created");

    private static ReportDraft ReportDraft() => new(new ReportSpec(
        "Tasks", Interpretation(), [], [], [], null));

    private static async Task<HarnessException> ThrowsHarness(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HarnessException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected HarnessException.");
    }

    private sealed class RecordingQwenJsonClient : IQwenJsonClient
    {
        private readonly object _response;

        public RecordingQwenJsonClient(object response) => _response = response;

        public string SystemPrompt { get; private set; } = "";
        public object? Input { get; private set; }
        public int MaxTokens { get; private set; }

        public Task<T> CompleteAsync<T>(
            string systemPrompt,
            object input,
            int maxTokens,
            CancellationToken ct)
        {
            SystemPrompt = systemPrompt;
            Input = input;
            MaxTokens = maxTokens;
            return Task.FromResult((T)_response);
        }
    }
}
