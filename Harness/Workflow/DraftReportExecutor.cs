#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class DraftReportExecutor : Executor<WorkflowState, WorkflowState>
{
    private readonly ReportDraftService _reports;
    public DraftReportExecutor(ReportDraftService reports) : base("report") => _reports = reports;
    public override async ValueTask<WorkflowState> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.IsTerminal) return state;
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        var startedAt = WorkflowExecution.Start(state.Context);
        try
        {
            var outcome = await _reports.CreateAsync(state.Request.Question, state.Context,
                WorkflowLimits.MaxModelCalls - state.ModelCalls, state.Context.Cancellation).ConfigureAwait(false);
            if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
            if (outcome.ModelCalls < 1 || outcome.ModelCalls > 2 || outcome.Repairs < 0 || outcome.Repairs > WorkflowLimits.MaxReportRepairs ||
                state.ModelCalls + outcome.ModelCalls > WorkflowLimits.MaxModelCalls)
                return WorkflowExecution.Cancelled(state);
            WorkflowExecution.AddStep(state.Context, startedAt, "draft_report", "ok");
            var validatedAt = WorkflowExecution.Start(state.Context);
            WorkflowExecution.AddStep(state.Context, validatedAt, "validate_report", "ok");
            return WorkflowExecution.Next(state, report: outcome.Spec,
                modelCalls: state.ModelCalls + outcome.ModelCalls, reportRepairs: state.ReportRepairs + outcome.Repairs);
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception)
        {
            var validationError = exception.ValidationErrors.FirstOrDefault();
            WorkflowExecution.AddStep(state.Context, startedAt,
                validationError is null ? "draft_report" : "validate_report",
                "error", null, validationError ?? exception.Error);
            return WorkflowExecution.Error(state, exception);
        }
        catch (Exception) { return WorkflowExecution.Unexpected(state, "draft_report", startedAt); }
    }
}
