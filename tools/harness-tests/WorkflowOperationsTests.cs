#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class WorkflowOperationsTests
{
    private static readonly DateTimeOffset FixedNow =
        DateTimeOffset.Parse("2026-09-30T23:59:59+03:00");

    public static async Task ConfirmedEmployeesUseVerifiedServerBindings()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(
            executor,
            new EmployeeCandidate(101, "Иванов Иван", "А"),
            new EmployeeCandidate(202, "Босов Александр", "Б"));
        var context = Context();

        await operations.PrepareAsync(new AnalysisRequest("Сравни",
        [
            new EntitySelection("Иванов", 101),
            new EntitySelection("Босов", 202)
        ]), context, CancellationToken.None);
        await operations.ExecuteSqlAsync(
            Plan("personal_instruction_count", Months(12)),
            new SqlDraft("""
                select count(*) n from public.sungero_wf_task
                where performer_id in (@selected_employee_1, @selected_employee_2)
                  and created >= @from and created < @to
                """, "personal_instruction_count"),
            context,
            CancellationToken.None);

        Check.Equal(1, executor.CallCount);
        Check.Equal(101L, executor.LastQuery!.Parameters["selected_employee_1"].GetInt64());
        Check.Equal(202L, executor.LastQuery.Parameters["selected_employee_2"].GetInt64());
    }

    public static async Task MissingConfirmedSelectionBindingStopsBeforeQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(
            executor,
            new EmployeeCandidate(101, "Иванов Иван", "А"),
            new EmployeeCandidate(202, "Босов Александр", "Б"));
        var context = Context();
        await operations.PrepareAsync(new AnalysisRequest("Сравни",
        [
            new EntitySelection("Иванов", 101),
            new EntitySelection("Босов", 202)
        ]), context, CancellationToken.None);

        var error = await ThrowsHarness(() => operations.ExecuteSqlAsync(
            Plan("personal_instruction_count", Months(12)),
            new SqlDraft("""
                select count(*) n from public.sungero_wf_task
                where performer_id = @selected_employee_1
                  and created >= @from and created < @to
                """, "personal_instruction_count"),
            context,
            CancellationToken.None));

        Check.Equal("missing_selection_binding", error.Code);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task MissingPeriodBindingStopsBeforeQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(executor);

        var error = await ThrowsHarness(() => operations.ExecuteSqlAsync(
            Plan("generic_query", Months(12)),
            new SqlDraft("select count(*) n from public.sungero_wf_task", "generic_query"),
            Context(),
            CancellationToken.None));

        Check.Equal("missing_period_binding", error.Code);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task PreflightRejectsMissingPeriodBindingWithoutQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(executor);

        var result = await operations.PreflightSqlAsync(
            Plan("generic_query", Months(12)),
            new SqlDraft("select count(*) n from public.sungero_wf_task", "generic_query"),
            Context(),
            CancellationToken.None);

        Check.True(!result.Ok);
        Check.Equal("missing_period_binding", result.Errors[0].Code);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task PreflightAcceptsPeriodBindingsWithoutQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(executor);

        var result = await operations.PreflightSqlAsync(
            Plan("generic_query", Months(12)),
            new SqlDraft("""
                select count(*) n from public.sungero_wf_task
                where created >= @from and created < @to
                """, "generic_query"),
            Context(),
            CancellationToken.None);

        Check.True(result.Ok);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task PreflightRejectsMissingSelectionBindingWithoutQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(
            executor,
            new EmployeeCandidate(101, "Иванов Иван", "А"),
            new EmployeeCandidate(202, "Босов Александр", "Б"));
        var context = Context();
        await operations.PrepareAsync(new AnalysisRequest("Сравни",
        [
            new EntitySelection("Иванов", 101),
            new EntitySelection("Босов", 202)
        ]), context, CancellationToken.None);

        var result = await operations.PreflightSqlAsync(
            Plan("personal_instruction_count", Months(12)),
            new SqlDraft("""
                select count(*) n from public.sungero_wf_task
                where performer_id = @selected_employee_1
                  and created >= @from and created < @to
                """, "personal_instruction_count"),
            context,
            CancellationToken.None);

        Check.True(!result.Ok);
        Check.Equal("missing_selection_binding", result.Errors[0].Code);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task AmbiguousEmployeeResolutionReturnsCandidatesWithoutQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(
            executor,
            new EmployeeCandidate(101, "Иванов Иван", "А"),
            new EmployeeCandidate(202, "Иванов Пётр", "Б"));

        var result = await operations.ResolveAsync(
            Plan("generic_query", All(), employeeMentions: ["Иванов"]),
            Context(),
            CancellationToken.None);

        Check.Equal(2, result.Candidates.Length);
        Check.Equal(0, result.Employees.Length);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task ConfirmedEmployeeIsNotResolvedAgain()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(
            executor,
            new EmployeeCandidate(101, "Иванов Иван", "А"),
            new EmployeeCandidate(202, "Иванов Пётр", "Б"));
        var context = Context();
        await operations.PrepareAsync(
            new AnalysisRequest("Покажи", [new EntitySelection("Иванов", 101)]),
            context,
            CancellationToken.None);

        var result = await operations.ResolveAsync(
            Plan("generic_query", All(), employeeMentions: ["Иванов"]),
            context,
            CancellationToken.None);

        Check.Equal(0, result.Candidates.Length);
        Check.Equal(1, result.Employees.Length);
    }

    public static async Task ResolutionAfterResultIsRejectedBeforeStateMutation()
    {
        var executor = new CapturingQueryExecutor();
        var resolver = new FakeEmployeeResolver(
            [new EmployeeCandidate(101, "Иванов Иван", "А")]);
        var operations = CreateOperations(executor, resolver);
        var context = Context();
        await operations.ExecuteSqlAsync(
            Plan("generic_query", All()),
            new SqlDraft("select count(*) n from public.sungero_wf_task", "generic_query"),
            context,
            CancellationToken.None);

        var error = await ThrowsHarness(() => operations.ResolveAsync(
            Plan("generic_query", All(), employeeMentions: ["Иванов"]),
            context,
            CancellationToken.None));

        Check.Equal("context_locked", error.Code);
        Check.Equal(0, resolver.SearchCallCount);
        Check.Equal(0, context.Candidates.Count);
        Check.Equal(0, context.ResolvedSelections.Count);
        Check.Equal(1, executor.CallCount);
    }

    public static async Task RelationOutsideCatalogStopsBeforeQuery()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(executor);

        var error = await ThrowsHarness(() => operations.ExecuteSqlAsync(
            Plan("generic_query", All()),
            new SqlDraft("select * from private.employee_payroll", "generic_query"),
            Context(),
            CancellationToken.None));

        Check.Equal("relation_not_allowed", error.Code);
        Check.Equal(0, executor.CallCount);
    }

    public static async Task UnsafeSqlStaysRejectedBeforeQuery()
    {
        foreach (var sql in new[]
        {
            "insert into public.sungero_wf_task values (1)",
            "update public.sungero_wf_task set id = 1",
            "delete from public.sungero_wf_task",
            "create table private.bad(id int)",
            "select nextval('private.sequence')"
        })
        {
            var executor = new CapturingQueryExecutor();
            var operations = CreateOperations(executor);
            var error = await ThrowsHarness(() => operations.ExecuteSqlAsync(
                Plan("generic_query", All()),
                new SqlDraft(sql, "generic_query"),
                Context(),
                CancellationToken.None));

            Check.True(error.Code is "unsafe_sql" or "unsupported_sql");
            Check.Equal(0, executor.CallCount);
        }
    }

    public static async Task DashboardStillRejectsConfirmedSelections()
    {
        var executor = new CapturingQueryExecutor();
        var operations = CreateOperations(executor, new EmployeeCandidate(101, "Иванов Иван", "А"));
        var context = Context();
        await operations.PrepareAsync(
            new AnalysisRequest("Покажи", [new EntitySelection("Иванов", 101)]),
            context,
            CancellationToken.None);

        var error = await ThrowsHarness(() => operations.ExecuteDashboardAsync(
            Plan("execution_discipline", Months(12), AnalysisDataRoute.DashboardMetric, "execution_discipline"),
            context,
            CancellationToken.None));

        Check.Equal("selection_not_supported", error.Code);
    }

    private static AnalysisOperations CreateOperations(
        CapturingQueryExecutor executor,
        params EmployeeCandidate[] employees)
    {
        var catalog = LoadCatalog();
        return new AnalysisOperations(new ToolDispatcher(
            catalog,
            new FakeEmployeeResolver(employees),
            executor,
            (_, _, _) => Task.FromResult(Query("dashboard"))));
    }

    private static AnalysisOperations CreateOperations(
        CapturingQueryExecutor executor,
        FakeEmployeeResolver resolver)
    {
        var catalog = LoadCatalog();
        return new AnalysisOperations(new ToolDispatcher(
            catalog,
            resolver,
            executor,
            (_, _, _) => Task.FromResult(Query("dashboard"))));
    }

    private static RunContext Context() =>
        new("workflow-operations", new FixedClock(FixedNow), CancellationToken.None);

    private static AnalysisPlan Plan(
        string metricId,
        PeriodSpec period,
        AnalysisDataRoute route = AnalysisDataRoute.GeneratedSql,
        string? dashboardMetric = null,
        string[]? employeeMentions = null) =>
        AnalysisPlan.Snapshot(
            metricId,
            period,
            route,
            dashboardMetric,
            employeeMentions ?? [],
            []);

    private static PeriodSpec All() => new("all", null, null, null);
    private static PeriodSpec Months(int months) => new("months", months, null, null);

    private static async Task<HarnessError> ThrowsHarness(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HarnessException exception)
        {
            return exception.Error;
        }

        throw new InvalidOperationException("Expected HarnessException.");
    }

    private static AnalyticsCatalog LoadCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "armgov-standalone.csproj")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Repository root was not found.");
        return AnalyticsCatalog.Load(Path.Combine(directory.FullName, "Harness", "Catalog", "catalog.json"));
    }

    private static QueryResult Query(string source) => new(
        source,
        [new ColumnSpec("n", "Количество", "number")],
        [[JsonSerializer.SerializeToElement(1)]],
        "select fixture",
        FixedNow,
        new Truncation(false, false, [], false),
        []);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeEmployeeResolver : IEmployeeResolver
    {
        private readonly EmployeeCandidate[] _employees;
        public FakeEmployeeResolver(EmployeeCandidate[] employees) => _employees = employees;
        public int SearchCallCount { get; private set; }
        public Task<EmployeeCandidate[]> SearchAsync(string[] tokens, CancellationToken ct) =>
            Task.FromResult(Search());
        public Task<EmployeeCandidate?> GetAsync(long id, CancellationToken ct) =>
            Task.FromResult(_employees.FirstOrDefault(item => item.Id == id));

        private EmployeeCandidate[] Search()
        {
            SearchCallCount++;
            return _employees;
        }
    }

    private sealed class CapturingQueryExecutor : IQueryExecutor
    {
        public int CallCount { get; private set; }
        public QuerySpec? LastQuery { get; private set; }
        public Task<QueryResult> ExecuteAsync(QuerySpec query, CancellationToken ct)
        {
            CallCount++;
            LastQuery = query;
            return Task.FromResult(Query("sql"));
        }
    }
}
