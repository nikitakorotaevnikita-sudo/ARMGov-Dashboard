#nullable enable

namespace ArmGov.Harness;

public enum AnalysisDataRoute
{
    DashboardMetric,
    GeneratedSql
}

public sealed record AnalysisPlan(
    string MetricId,
    PeriodSpec Period,
    AnalysisDataRoute Route,
    string? DashboardMetric,
    string[] EmployeeMentions,
    string[] RelationHints);

public sealed record SqlDraft(string Sql, string MetricId);

public sealed record ReportDraft(ReportSpec Report);

internal sealed record WorkflowState(
    AnalysisRequest Request,
    RunContext Context,
    AnalysisPlan? Plan,
    SqlDraft? Sql,
    ReportSpec? Report,
    AnalysisResponse? Terminal,
    int ModelCalls,
    int SqlRepairs,
    int ReportRepairs)
{
    public bool IsTerminal => Terminal is not null;
}
