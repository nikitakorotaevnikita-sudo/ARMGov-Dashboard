#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class WorkflowContractTests
{
    private static readonly DateTimeOffset AsOf =
        DateTimeOffset.Parse("2026-09-20T12:00:00+04:00");
    private static readonly IReadOnlySet<string> DashboardMetrics =
        new HashSet<string>(StringComparer.Ordinal) { "execution_discipline" };

    public static void DashboardRouteRequiresKnownDashboardMetric()
    {
        var plan = new AnalysisPlan(
            "execution_discipline",
            new PeriodSpec("months", 12, null, null),
            AnalysisDataRoute.DashboardMetric,
            null,
            [],
            []);

        var validation = WorkflowContractValidator.ValidatePlan(plan, AsOf, DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("invalid_dashboard_route", validation.Errors[0].Code);
    }

    public static void GeneratedSqlRejectsDashboardMetricName()
    {
        var plan = new AnalysisPlan(
            "generic_query",
            new PeriodSpec("months", 12, null, null),
            AnalysisDataRoute.GeneratedSql,
            "leaders",
            [],
            ["public.sungero_wf_task"]);

        Check.True(!WorkflowContractValidator.ValidatePlan(plan, AsOf, DashboardMetrics).Ok);
    }

    public static void EmptyMetricIsRejected()
    {
        var validation = WorkflowContractValidator.ValidatePlan(
            Plan(metricId: " "), AsOf, DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("invalid_metric_id", validation.Errors[0].Code);
    }

    public static void ZeroMonthsReturnsValidationError()
    {
        var validation = WorkflowContractValidator.ValidatePlan(
            Plan(period: new PeriodSpec("months", 0, null, null)), AsOf, DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("invalid_period", validation.Errors[0].Code);
    }

    public static void ReversedRangeReturnsValidationError()
    {
        var validation = WorkflowContractValidator.ValidatePlan(
            Plan(period: new PeriodSpec("range", null, AsOf, AsOf.AddDays(-1))),
            AsOf,
            DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("invalid_period", validation.Errors[0].Code);
    }

    public static void MoreThanTenEmployeeMentionsAreRejected()
    {
        var validation = WorkflowContractValidator.ValidatePlan(
            Plan(employeeMentions: Enumerable.Range(1, 11).Select(id => $"Employee {id}").ToArray()),
            AsOf,
            DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("too_many_employee_mentions", validation.Errors[0].Code);
    }

    public static void MoreThanTwelveRelationHintsAreRejected()
    {
        var validation = WorkflowContractValidator.ValidatePlan(
            Plan(relationHints: Enumerable.Range(1, 13).Select(id => $"public.table_{id}").ToArray()),
            AsOf,
            DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("too_many_relation_hints", validation.Errors[0].Code);
    }

    public static void RelationHintMustUseSchemaTableSyntax()
    {
        var validation = WorkflowContractValidator.ValidatePlan(
            Plan(relationHints: ["sungero_wf_task"]), AsOf, DashboardMetrics);

        Check.True(!validation.Ok);
        Check.Equal("invalid_relation_hint", validation.Errors[0].Code);
    }

    public static void BlankSqlIsRejected()
    {
        var validation = WorkflowContractValidator.ValidateSqlDraft(
            new SqlDraft(" ", "generic_query"), Plan());

        Check.True(!validation.Ok);
        Check.Equal("invalid_sql", validation.Errors[0].Code);
    }

    public static void SqlOverTwentyThousandCharactersIsRejected()
    {
        var validation = WorkflowContractValidator.ValidateSqlDraft(
            new SqlDraft(new string('x', 20_001), "generic_query"), Plan());

        Check.True(!validation.Ok);
        Check.Equal("invalid_sql", validation.Errors[0].Code);
    }

    public static void SqlDraftMetricMustMatchPlanOrdinally()
    {
        var validation = WorkflowContractValidator.ValidateSqlDraft(
            new SqlDraft("select 1", "GENERIC_QUERY"), Plan());

        Check.True(!validation.Ok);
        Check.Equal("metric_mismatch", validation.Errors[0].Code);
    }

    public static void AnalysisDataRouteUsesCamelCaseJsonStringsAndRejectsNumbers()
    {
        Check.Equal("\"dashboardMetric\"", JsonSerializer.Serialize(
            AnalysisDataRoute.DashboardMetric,
            HarnessJson.Options));
        Check.Equal("\"generatedSql\"", JsonSerializer.Serialize(
            AnalysisDataRoute.GeneratedSql,
            HarnessJson.Options));
        Check.Throws<JsonException>(() => JsonSerializer.Deserialize<AnalysisDataRoute>(
            "0",
            HarnessJson.Options));
    }

    public static void WorkflowStateIsTerminalOnlyWhenItHasAResponse()
    {
        var context = new RunContext("workflow-contract-test", TimeProvider.System, CancellationToken.None);
        var initial = new WorkflowState(
            new AnalysisRequest("Question", []),
            context,
            null,
            null,
            null,
            null,
            0,
            0,
            0);
        var terminal = initial with
        {
            Terminal = new AnalysisResponse(
                context.RunId,
                "failed",
                null,
                [],
                [],
                0,
                [],
                null,
                new HarnessError("failed", "Failed", false))
        };

        Check.True(!initial.IsTerminal);
        Check.True(terminal.IsTerminal);
    }

    public static void WorkflowLimitsSupportTheSpecifiedRunBudget()
    {
        Check.Equal(5, WorkflowLimits.MaxModelCalls);
        Check.Equal(1, WorkflowLimits.MaxSqlRepairs);
        Check.Equal(1, WorkflowLimits.MaxReportRepairs);
    }

    private static AnalysisPlan Plan(
        string metricId = "generic_query",
        PeriodSpec? period = null,
        string[]? employeeMentions = null,
        string[]? relationHints = null) =>
        new(
            metricId,
            period ?? new PeriodSpec("months", 12, null, null),
            AnalysisDataRoute.GeneratedSql,
            null,
            employeeMentions ?? [],
            relationHints ?? ["public.sungero_wf_task"]);
}
