#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class WorkflowTests
{
    // Removing the dashboard branch, report rendering, or terminal mapping makes this fail.
    public static async Task DashboardPlanProducesCompletedEvidenceBoundResponse()
    {
        var model = new ScriptedModel(
            dashboard: Plan(AnalysisDataRoute.DashboardMetric, "execution_discipline"),
            report: ValidReport());
        var operations = new ScriptedOperations { Dashboard = Page(1) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("completed", response.Status);
        Check.Equal("r1", response.Datasets.Single().ResultId);
        Check.Equal(1, operations.DashboardCalls);
        Check.Equal(0, model.SqlCalls);
        Check.Equal(1, model.ReportCalls);
    }

    // Removing SQL draft validation or its one-repair limit makes this fail.
    public static async Task InvalidSqlIsRepairedOnceBeforeExecution()
    {
        var model = new ScriptedModel(
            sql: new SqlDraft("", "generic_query"),
            repairedSql: Sql(),
            report: ValidReport());
        var operations = new ScriptedOperations { Sql = Page(1) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("completed", response.Status);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal(1, operations.SqlCalls);
    }

    // Skipping the generated-SQL route or invoking it more than once makes this fail.
    public static async Task ValidSqlPlanExecutesOnceAndCompletes()
    {
        var model = new ScriptedModel(sql: Sql(), report: ValidReport());
        var operations = new ScriptedOperations { Sql = Page(1) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("completed", response.Status);
        Check.Equal(1, model.SqlCalls);
        Check.Equal(0, model.SqlRepairCalls);
        Check.Equal(1, operations.SqlCalls);
    }

    // Allowing a second SQL repair or executing an invalid draft makes this fail.
    public static async Task TwoInvalidSqlDraftsFailBeforeDataExecution()
    {
        var model = new ScriptedModel(
            sql: new SqlDraft("", "generic_query"),
            repairedSql: new SqlDraft("", "generic_query"));
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("failed", response.Status);
        Check.Equal(0, operations.SqlCalls);
        Check.Equal(1, model.SqlRepairCalls);
    }

    // Continuing into acquire/report after ambiguity makes this fail.
    public static async Task AmbiguousEmployeeStopsBeforeDataAndReport()
    {
        var model = new ScriptedModel(sql: Sql());
        var operations = new ScriptedOperations
        {
            Resolution = new EntityResolutionResult([], [new EmployeeCandidate(1, "Иванов Иван", "A")])
        };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("needs_clarification", response.Status);
        Check.Equal(0, model.SqlCalls);
        Check.Equal(0, model.ReportCalls);
        Check.Equal(0, operations.SqlCalls);
    }

    // Starting report drafting for an empty page makes this fail.
    public static async Task EmptyPageStopsWithNoData()
    {
        var model = new ScriptedModel(sql: Sql());
        var operations = new ScriptedOperations { Sql = Page(0) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("no_data", response.Status);
        Check.Equal(0, model.ReportCalls);
    }

    // Mapping pre-data model faults to incomplete makes this fail.
    public static async Task ProviderFailureBeforeDataIsFailed()
    {
        var model = new ScriptedModel { PlanFailure = Error("provider_down") };
        var response = await Create(model, new ScriptedOperations()).RunAsync(Request(), CancellationToken.None);

        Check.Equal("failed", response.Status);
        Check.Equal("provider_down", response.Error!.Code);
    }

    // Mapping a report failure after an actual stored result to failed makes this fail.
    public static async Task ReportFailureAfterDataIsIncomplete()
    {
        var model = new ScriptedModel(sql: Sql()) { ReportFailure = Error("provider_down") };
        var operations = new ScriptedOperations { Sql = Page(1) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("incomplete", response.Status);
        Check.Equal("provider_down", response.Steps.Last().Error!.Code);
        Check.Equal(1, response.Datasets.Length);
    }

    // Ignoring cancellation and allowing a later data operation makes this fail.
    public static async Task CancellationReturnsIncompleteWithoutDataExecution()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var operations = new ScriptedOperations();
        var response = await Create(new ScriptedModel(), operations).RunAsync(Request(), cancelled.Token);

        Check.Equal("incomplete", response.Status);
        Check.Equal(0, operations.SqlCalls);
        Check.Equal(0, operations.DashboardCalls);
    }

    // Dropping QueryResult truncation warnings while copying a page makes this fail.
    public static async Task TruncationWarningSurvivesCompletedResponse()
    {
        var model = new ScriptedModel(sql: Sql(), report: ValidReport());
        var operations = new ScriptedOperations { Sql = Page(1, ["результат усечён"]) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("completed", response.Status);
        Check.True(response.Warnings.Contains("результат усечён"));
    }

    private static AnalysisWorkflow Create(ScriptedModel model, ScriptedOperations operations) =>
        new(model, operations, new ReportDraftService(model), TimeProvider.System);

    private static AnalysisRequest Request() => new("Покажи данные", []);
    private static AnalysisPlan Plan(AnalysisDataRoute route = AnalysisDataRoute.GeneratedSql, string? dashboard = null) =>
        AnalysisPlan.Snapshot(route == AnalysisDataRoute.DashboardMetric ? "execution_discipline" : "generic_query",
            new PeriodSpec("all", null, null, null), route, dashboard, [],
            route == AnalysisDataRoute.DashboardMetric ? [] : ["public.sungero_wf_task"]);
    private static SqlDraft Sql() => new("select 1 from public.sungero_wf_task", "generic_query");
    private static ReportDraft ValidReport() => new(new ReportSpec("Данные",
        new Interpretation("generic_query", "Данные", "шт", null, null, null),
        [new BlockSpec("table", "r1", ["n"], null, null)], [], [], null));
    private static ResultPage Page(int rows, string[]? warnings = null) => new("script", "r1",
        [new ColumnSpec("n", "N", "number")], Enumerable.Range(0, rows)
            .Select(item => new[] { JsonSerializer.SerializeToElement(item + 1) }).ToArray(), 0, rows, false,
        new Truncation(false, false, [], false), warnings ?? []);
    private static HarnessException Error(string code) => new(new HarnessError(code, "scripted failure", false));

    private sealed class ScriptedOperations : IAnalysisOperations
    {
        public ResultPage Dashboard { get; set; } = Page(1);
        public ResultPage Sql { get; set; } = Page(1);
        public EntityResolutionResult Resolution { get; set; } = new([], []);
        public int DashboardCalls { get; private set; }
        public int SqlCalls { get; private set; }
        public Task<PreparationResult> PrepareAsync(AnalysisRequest request, RunContext context, CancellationToken ct) => Task.FromResult(new PreparationResult([]));
        public Task<EntityResolutionResult> ResolveAsync(AnalysisPlan plan, RunContext context, CancellationToken ct) => Task.FromResult(Resolution);
        public Task<ResultPage> ExecuteDashboardAsync(AnalysisPlan plan, RunContext context, CancellationToken ct)
        {
            DashboardCalls++;
            return Task.FromResult(Store(Dashboard, plan, context));
        }
        public Task<ResultPage> ExecuteSqlAsync(AnalysisPlan plan, SqlDraft draft, RunContext context, CancellationToken ct)
        {
            SqlCalls++;
            return Task.FromResult(Store(Sql, plan, context));
        }
        private static ResultPage Store(ResultPage page, AnalysisPlan plan, RunContext context)
        {
            context.Interpretation = new Interpretation(plan.MetricId, "Данные", "шт", null, null, null);
            context.Results.Add(new QueryResult("script", page.Columns, page.Rows, "select fixture",
                DateTimeOffset.UtcNow, page.Truncation, page.Warnings));
            return context.Results.Page(context.RunId, "r1", 0, 20);
        }
    }

    private sealed class ScriptedModel : IAnalysisStageModel
    {
        private readonly AnalysisPlan _plan;
        private readonly SqlDraft _sql;
        private readonly SqlDraft _repairedSql;
        private readonly ReportDraft _report;
        public ScriptedModel(AnalysisPlan? dashboard = null, SqlDraft? sql = null, SqlDraft? repairedSql = null, ReportDraft? report = null)
        { _plan = dashboard ?? Plan(); _sql = sql ?? Sql(); _repairedSql = repairedSql ?? _sql; _report = report ?? ValidReport(); }
        public HarnessException? PlanFailure { get; init; }
        public HarnessException? ReportFailure { get; init; }
        public int SqlCalls { get; private set; }
        public int SqlRepairCalls { get; private set; }
        public int ReportCalls { get; private set; }
        public Task<AnalysisPlan> PlanAsync(PlanningInput input, CancellationToken ct) => PlanFailure is null ? Task.FromResult(_plan) : Task.FromException<AnalysisPlan>(PlanFailure);
        public Task<SqlDraft> DraftSqlAsync(SqlGenerationInput input, CancellationToken ct) { SqlCalls++; return Task.FromResult(_sql); }
        public Task<SqlDraft> RepairSqlAsync(SqlRepairInput input, CancellationToken ct) { SqlRepairCalls++; return Task.FromResult(_repairedSql); }
        public Task<ReportDraft> DraftReportAsync(ReportGenerationInput input, CancellationToken ct) { ReportCalls++; return ReportFailure is null ? Task.FromResult(_report) : Task.FromException<ReportDraft>(ReportFailure); }
        public Task<ReportDraft> RepairReportAsync(ReportRepairInput input, CancellationToken ct) => Task.FromResult(_report);
    }
}
