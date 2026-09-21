#nullable enable

using System.Text.Json;
using System.Net.Http;
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

    // Binding failures must repair before ExecuteSqlAsync, using the dispatcher error code.
    public static async Task MissingPeriodBindingIsRepairedOnceBeforeExecution()
    {
        var model = new ScriptedModel(RangePlan(), UnboundSql(), BoundSql(), ValidReport());
        var operations = new ScriptedOperations { Sql = Page(1) };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("completed", response.Status);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal("missing_period_binding", model.SqlRepairInput!.Errors.Single().Code);
        Check.Equal(1, operations.SqlCalls);
        Check.Equal(2, operations.PreflightCalls);
    }

    // A still-unbound repair must not call ExecuteSqlAsync or spend a second repair.
    public static async Task SecondUnboundSqlFailsBeforeDataExecution()
    {
        var model = new ScriptedModel(RangePlan(), UnboundSql(), UnboundSql());
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("failed", response.Status);
        Check.Equal("missing_period_binding", response.Error!.Code);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal(0, operations.SqlCalls);
    }

    // Missing a confirmed employee parameter is the same one-repair binding path.
    public static async Task MissingSelectionBindingIsRepairedOnceBeforeExecution()
    {
        var model = new ScriptedModel(
            RangePlan(),
            new SqlDraft("select 1 from public.sungero_wf_task where created >= @from and created < @to and performer_id = @selected_employee_1", "generic_query"),
            new SqlDraft("select 1 from public.sungero_wf_task where created >= @from and created < @to and performer_id in (@selected_employee_1, @selected_employee_2)", "generic_query"),
            ValidReport());
        var operations = new ScriptedOperations
        {
            Resolution = new EntityResolutionResult(
            [
                new VerifiedEmployee(7, "Иванов Иван", "selected_employee_1"),
                new VerifiedEmployee(8, "Босов Александр", "selected_employee_2")
            ], []),
            Sql = Page(1)
        };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("completed", response.Status);
        Check.Equal("missing_selection_binding", model.SqlRepairInput!.Errors.Single().Code);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal(1, operations.SqlCalls);
    }

    // Cancellation after a successful binding repair must not reach ExecuteSqlAsync.
    public static async Task CancellationAfterBindingRepairStopsBeforeExecution()
    {
        using var cancelled = new CancellationTokenSource();
        var model = new ScriptedModel(RangePlan(), UnboundSql(), BoundSql())
        {
            AfterSqlRepair = cancelled.Cancel
        };
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), cancelled.Token);

        Check.Equal("incomplete", response.Status);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal(0, operations.SqlCalls);
    }

    // DTO validation and binding preflight share a single repair budget.
    public static async Task DtoAndBindingFailuresShareOneSqlRepair()
    {
        var model = new ScriptedModel(RangePlan(), new SqlDraft("", "generic_query"), UnboundSql());
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("failed", response.Status);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal("invalid_sql", model.SqlRepairInput!.Errors[0].Code);
        Check.Equal(0, operations.SqlCalls);
        Check.True(response.Error!.Code is "invalid_sql" or "missing_period_binding");
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

    // Omitting the SQL-path error record for a failed repair hides the terminal cause.
    public static async Task SqlRepairFailureRecordsExecuteSqlErrorAndStopsBeforeDatabase()
    {
        var model = new ScriptedModel(sql: new SqlDraft("", "generic_query"))
        {
            SqlRepairFailure = Error("provider_protocol_error")
        };
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("failed", response.Status);
        Check.Equal("provider_protocol_error", response.Error!.Code);
        Check.Equal(0, operations.SqlCalls);
        Check.Equal("execute_sql", response.Steps.Last().Tool);
        Check.Equal("provider_protocol_error", response.Steps.Last().Error!.Code);
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

    // Dropping verified employee bindings or schema fields from SQL input makes this fail.
    public static async Task SqlInputContainsVerifiedEmployeesAndHintedCatalogDefinitions()
    {
        var model = new ScriptedModel(
            sql: new SqlDraft("select 1 from public.sungero_wf_task where performer_id = @selected_employee_1", "generic_query"),
            report: ValidReport());
        var operations = new ScriptedOperations
        {
            Resolution = new EntityResolutionResult([new VerifiedEmployee(7, "Иванов Иван", "selected_employee_1")], [])
        };
        await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("selected_employee_1", model.SqlInput!.Employees.Single().ParameterName);
        Check.Equal("public.sungero_wf_task", model.SqlInput.Catalog.Relations.Single().Name);
        Check.True(model.SqlInput.Catalog.Relations.Single().Fields.Any(field => field.Name == "created"));
    }

    // Planning must use the complete safe catalog allowlists rather than executor-local samples.
    public static async Task PlanningInputContainsCatalogOwnedSafeMetricAndRelationSummaries()
    {
        var model = new ScriptedModel(sql: Sql(), report: ValidReport());
        await Create(model, new ScriptedOperations { Sql = Page(1) })
            .RunAsync(Request(), CancellationToken.None);

        var input = model.PlanningInput!;
        Check.Equal(6, input.Metrics.Length);
        Check.True(input.Metrics.Any(metric => metric.MetricId == "personal_instruction_count"));
        Check.Equal(
            "execution_discipline",
            input.Metrics.Single(metric => metric.MetricId == "execution_discipline").DashboardMetric);
        Check.True(input.Metrics.Any(metric => metric.MetricId == "generic_query"));

        var json = JsonSerializer.SerializeToElement(input, HarnessJson.Options);
        var relations = json.GetProperty("relations");
        Check.Equal(12, relations.GetArrayLength());
        Check.Equal(
            "public.sungero_company_jobtitle",
            relations[0].GetProperty("name").GetString());
        Check.True(relations.EnumerateArray().Any(relation =>
            relation.GetProperty("name").GetString() == "public.sungero_wf_task"));

        var serialized = json.GetRawText();
        Check.True(!serialized.Contains("\"fields\"", StringComparison.OrdinalIgnoreCase));
        Check.True(!serialized.Contains("password", StringComparison.OrdinalIgnoreCase));
        Check.True(!serialized.Contains("token", StringComparison.OrdinalIgnoreCase));
        Check.True(!serialized.Contains("connectionString", StringComparison.OrdinalIgnoreCase));
    }

    // Treating an unmatched mention like no mention allows an unsafe data call.
    public static async Task UnmatchedEmployeeStopsBeforeSqlAndReport()
    {
        var model = new ScriptedModel(sql: Sql());
        var operations = new ScriptedOperations
        {
            Resolution = new EntityResolutionResult([], [], ["Несуществующий сотрудник"])
        };
        var response = await Create(model, operations).RunAsync(Request(), CancellationToken.None);

        Check.Equal("needs_clarification", response.Status);
        Check.Equal(0, model.SqlCalls);
        Check.Equal(0, operations.SqlCalls);
        Check.Equal(0, model.ReportCalls);
    }

    // Executing SQL after a non-cooperative draft observes cancellation is unsafe.
    public static async Task CancellationAfterSqlDraftStopsBeforeExecution()
    {
        using var cancelled = new CancellationTokenSource();
        var model = new ScriptedModel(sql: Sql()) { AfterSql = cancelled.Cancel };
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), cancelled.Token);

        Check.Equal("incomplete", response.Status);
        Check.Equal(0, operations.SqlCalls);
    }

    // Executing repaired SQL after its non-cooperative response observes cancellation is unsafe.
    public static async Task CancellationAfterSqlRepairStopsBeforeExecution()
    {
        using var cancelled = new CancellationTokenSource();
        var model = new ScriptedModel(sql: new SqlDraft("", "generic_query"), repairedSql: Sql())
        {
            AfterSqlRepair = cancelled.Cancel
        };
        var operations = new ScriptedOperations();
        var response = await Create(model, operations).RunAsync(Request(), cancelled.Token);

        Check.Equal("incomplete", response.Status);
        Check.Equal(1, model.SqlRepairCalls);
        Check.Equal(0, operations.SqlCalls);
    }

    // Starting report repair after a non-cooperative first report ignores cancellation.
    public static async Task CancellationAfterReportDraftStopsBeforeRepairAndFinish()
    {
        using var cancelled = new CancellationTokenSource();
        var model = new ScriptedModel(report: ValidReport()) { AfterReport = cancelled.Cancel };
        var operations = new ScriptedOperations { Sql = Page(1) };
        var response = await Create(model, operations).RunAsync(Request(), cancelled.Token);

        Check.Equal("incomplete", response.Status);
        Check.Equal(0, model.ReportRepairCalls);
    }

    // Letting transport faults escape bypasses the HTTP response contract.
    public static async Task UnexpectedExceptionsMapBeforeAndAfterStoredData()
    {
        var before = await Create(new ScriptedModel { PlanUnexpected = new HttpRequestException("endpoint") }, new ScriptedOperations())
            .RunAsync(Request(), CancellationToken.None);
        var after = await Create(new ScriptedModel(sql: Sql()) { ReportUnexpected = new HttpRequestException("endpoint") },
            new ScriptedOperations { Sql = Page(1) }).RunAsync(Request(), CancellationToken.None);

        Check.Equal("failed", before.Status);
        Check.Equal("incomplete", after.Status);
        Check.Equal("workflow_failure", before.Error!.Code);
        Check.Equal("workflow_failure", after.Error!.Code);
    }

    // Reporting dashboard faults as execute_sql obscures the failed trusted boundary.
    public static async Task DashboardFailureUsesDashboardStepName()
    {
        var operations = new ScriptedOperations { DashboardFailure = Error("dashboard_metric_failed") };
        var response = await Create(new ScriptedModel(dashboard: Plan(AnalysisDataRoute.DashboardMetric, "execution_discipline")), operations)
            .RunAsync(Request(), CancellationToken.None);

        Check.Equal("dashboard_metric", response.Steps.Last().Tool);
    }

    private static AnalysisWorkflow Create(ScriptedModel model, ScriptedOperations operations) =>
        new(model, operations, new ReportDraftService(model), Catalog(), TimeProvider.System);

    private static AnalyticsCatalog Catalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "armgov-standalone.csproj"))) directory = directory.Parent;
        return AnalyticsCatalog.Load(Path.Combine(directory!.FullName, "Harness", "Catalog", "catalog.json"));
    }

    private static AnalysisRequest Request() => new("Покажи данные", []);
    private static AnalysisPlan Plan(AnalysisDataRoute route = AnalysisDataRoute.GeneratedSql, string? dashboard = null) =>
        AnalysisPlan.Snapshot(route == AnalysisDataRoute.DashboardMetric ? "execution_discipline" : "generic_query",
            new PeriodSpec("all", null, null, null), route, dashboard, [],
            route == AnalysisDataRoute.DashboardMetric ? [] : ["public.sungero_wf_task"]);
    private static AnalysisPlan RangePlan() =>
        AnalysisPlan.Snapshot("generic_query",
            new PeriodSpec("range", null,
                DateTimeOffset.Parse("1990-01-01T00:00:00+00:00"),
                DateTimeOffset.Parse("1990-02-01T00:00:00+00:00")),
            AnalysisDataRoute.GeneratedSql, null, [], ["public.sungero_wf_task"]);
    private static SqlDraft Sql() => new("select 1 from public.sungero_wf_task", "generic_query");
    private static SqlDraft UnboundSql() => new("select 1 from public.sungero_wf_task", "generic_query");
    private static SqlDraft BoundSql() => new(
        "select 1 from public.sungero_wf_task where created >= @from and created < @to",
        "generic_query");
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
        public int PreflightCalls { get; private set; }
        public HarnessException? DashboardFailure { get; init; }
        public Task<PreparationResult> PrepareAsync(AnalysisRequest request, RunContext context, CancellationToken ct) => Task.FromResult(new PreparationResult([]));
        public Task<EntityResolutionResult> ResolveAsync(AnalysisPlan plan, RunContext context, CancellationToken ct)
        {
            foreach (var employee in Resolution.Employees)
                context.RegisterResolved(employee.Name, new EmployeeCandidate(employee.Id, employee.Name, ""));
            return Task.FromResult(Resolution);
        }
        public Task<ResultPage> ExecuteDashboardAsync(AnalysisPlan plan, RunContext context, CancellationToken ct)
        {
            DashboardCalls++;
            if (DashboardFailure is not null) return Task.FromException<ResultPage>(DashboardFailure);
            return Task.FromResult(Store(Dashboard, plan, context));
        }
        public Task<ResultPage> ExecuteSqlAsync(AnalysisPlan plan, SqlDraft draft, RunContext context, CancellationToken ct)
        {
            SqlCalls++;
            return Task.FromResult(Store(Sql, plan, context));
        }
        public Task<ValidationResult> PreflightSqlAsync(AnalysisPlan plan, SqlDraft draft, RunContext context, CancellationToken ct)
        {
            PreflightCalls++;
            var errors = new List<HarnessError>();
            var needsPeriod = string.Equals(plan.Period.Kind, "range", StringComparison.Ordinal)
                || string.Equals(plan.Period.Kind, "months", StringComparison.Ordinal)
                || plan.Period.From is not null
                || plan.Period.To is not null
                || context.Interpretation?.From is not null
                || context.Interpretation?.To is not null;
            if (needsPeriod &&
                (!SqlGuard.ReferencesParameter(draft.Sql, "from") ||
                 !SqlGuard.ReferencesParameter(draft.Sql, "to")))
            {
                errors.Add(new HarnessError(
                    "missing_period_binding",
                    "SQL с периодом должен ссылаться на @from и @to.",
                    true));
            }
            foreach (var selection in context.ResolvedSelections)
            {
                if (!SqlGuard.ReferencesParameter(draft.Sql, selection.ParameterName))
                {
                    errors.Add(new HarnessError(
                        "missing_selection_binding",
                        $"SQL должен фильтровать выбранного сотрудника через @{selection.ParameterName}.",
                        true));
                }
            }
            return Task.FromResult(new ValidationResult(errors.Count == 0, errors.ToArray()));
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
        public HarnessException? SqlRepairFailure { get; init; }
        public HarnessException? ReportFailure { get; init; }
        public Exception? PlanUnexpected { get; init; }
        public Exception? ReportUnexpected { get; init; }
        public Action? AfterSql { get; init; }
        public Action? AfterSqlRepair { get; init; }
        public Action? AfterReport { get; init; }
        public int SqlCalls { get; private set; }
        public int SqlRepairCalls { get; private set; }
        public int ReportCalls { get; private set; }
        public int ReportRepairCalls { get; private set; }
        public PlanningInput? PlanningInput { get; private set; }
        public SqlGenerationInput? SqlInput { get; private set; }
        public SqlRepairInput? SqlRepairInput { get; private set; }
        public Task<AnalysisPlan> PlanAsync(PlanningInput input, CancellationToken ct)
        {
            PlanningInput = input;
            return PlanUnexpected is not null
                ? Task.FromException<AnalysisPlan>(PlanUnexpected)
                : PlanFailure is null
                    ? Task.FromResult(_plan)
                    : Task.FromException<AnalysisPlan>(PlanFailure);
        }
        public Task<SqlDraft> DraftSqlAsync(SqlGenerationInput input, CancellationToken ct) { SqlCalls++; SqlInput = input; AfterSql?.Invoke(); return Task.FromResult(_sql); }
        public Task<SqlDraft> RepairSqlAsync(SqlRepairInput input, CancellationToken ct)
        {
            SqlRepairInput = input;
            SqlRepairCalls++;
            AfterSqlRepair?.Invoke();
            return SqlRepairFailure is null ? Task.FromResult(_repairedSql) : Task.FromException<SqlDraft>(SqlRepairFailure);
        }
        public Task<ReportDraft> DraftReportAsync(ReportGenerationInput input, CancellationToken ct) { ReportCalls++; AfterReport?.Invoke(); return ReportUnexpected is not null ? Task.FromException<ReportDraft>(ReportUnexpected) : ReportFailure is null ? Task.FromResult(_report) : Task.FromException<ReportDraft>(ReportFailure); }
        public Task<ReportDraft> RepairReportAsync(ReportRepairInput input, CancellationToken ct) { ReportRepairCalls++; return Task.FromResult(_report); }
    }
}
