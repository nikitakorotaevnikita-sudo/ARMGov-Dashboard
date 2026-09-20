#nullable enable

using System;
using System.Linq;
using System.Threading;

namespace ArmGov.Harness;

internal static class WorkflowExecution
{
    internal static WorkflowState Next(WorkflowState state, AnalysisPlan? plan = null,
        SqlDraft? sql = null, ReportSpec? report = null, AnalysisResponse? terminal = null,
        int? modelCalls = null, int? sqlRepairs = null, int? reportRepairs = null) => new(
            state.Request, state.Context, plan ?? state.Plan, sql ?? state.Sql, report ?? state.Report,
            terminal ?? state.Terminal, modelCalls ?? state.ModelCalls, sqlRepairs ?? state.SqlRepairs,
            reportRepairs ?? state.ReportRepairs);

    internal static WorkflowState Terminal(WorkflowState state, string status, HarnessError? error = null,
        Clarification? clarification = null)
    {
        var response = new AnalysisResponse(state.Context.RunId, status, null, state.Context.Results.All(),
            state.Context.Steps.ToArray(), state.Context.ElapsedMs, state.Context.Warnings.ToArray(), clarification, error);
        return Next(state, terminal: response);
    }

    internal static WorkflowState Error(WorkflowState state, HarnessException exception)
    {
        var status = state.Context.HasStoredResults ? "incomplete" : "failed";
        return Terminal(state, status, exception.Error);
    }

    internal static WorkflowState Cancelled(WorkflowState state)
    {
        AddWarning(state.Context, "Выполнение отменено или бюджет времени исчерпан.");
        return Terminal(state, "incomplete");
    }

    internal static bool IsCancelled(WorkflowState state, CancellationToken ct) =>
        ct.IsCancellationRequested || state.Context.IsExpired;

    internal static void AddStep(RunContext context, string tool, string status,
        string? resultId = null, HarnessError? error = null) =>
        context.Steps.Add(new AgentStep(context.Steps.Count + 1, tool, status, 0, resultId, error));

    internal static void AddWarning(RunContext context, string warning)
    {
        if (!context.Warnings.Contains(warning, StringComparer.Ordinal))
            context.Warnings.Add(warning);
    }
}
