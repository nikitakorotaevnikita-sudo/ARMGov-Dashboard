#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class AcquireDataExecutor : Executor<WorkflowState, WorkflowState>
{
    private readonly IAnalysisStageModel _model;
    private readonly IAnalysisOperations _operations;
    private readonly AnalyticsCatalog _catalog;
    public AcquireDataExecutor(IAnalysisStageModel model, IAnalysisOperations operations, AnalyticsCatalog catalog) : base("acquire") { _model = model; _operations = operations; _catalog = catalog; }
    public override async ValueTask<WorkflowState> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.IsTerminal) return state;
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        var startedAt = WorkflowExecution.Start(state.Context);
        try
        {
            ResultPage page;
            WorkflowState next = state;
            if (state.Plan!.Route == AnalysisDataRoute.DashboardMetric)
            {
                page = await _operations.ExecuteDashboardAsync(state.Plan, state.Context, state.Context.Cancellation).ConfigureAwait(false);
                if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
                WorkflowExecution.AddStep(state.Context, startedAt, "dashboard_metric", "ok", page.ResultId);
            }
            else
            {
                if (state.ModelCalls >= WorkflowLimits.MaxModelCalls) return WorkflowExecution.Cancelled(state);
                var input = new SqlGenerationInput(state.Request.Question, state.Plan,
                    ContextInterpretation(state), state.Employees.ToArray(),
                    _catalog.CreateProjection(state.Plan.MetricId, state.Plan.RelationHints));
                SqlDraft draft;
                try { draft = await _model.DraftSqlAsync(input, state.Context.Cancellation).ConfigureAwait(false); }
                catch (HarnessException) { throw; }
                if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
                next = WorkflowExecution.Next(state, sql: draft, modelCalls: state.ModelCalls + 1);
                var valid = WorkflowContractValidator.ValidateSqlDraft(draft, state.Plan);
                var repaired = false;
                if (!valid.Ok)
                {
                    next = await RepairSqlAsync(next, input, draft, valid.Errors, startedAt).ConfigureAwait(false);
                    repaired = true;
                }
                if (next.IsTerminal) return next;
                if (WorkflowExecution.IsCancelled(next, ct)) return WorkflowExecution.Cancelled(next);
                if (!repaired)
                {
                    var preflight = await _operations.PreflightSqlAsync(
                        next.Plan!, next.Sql!, next.Context, next.Context.Cancellation).ConfigureAwait(false);
                    if (!preflight.Ok)
                    {
                        next = await RepairSqlAsync(next, input, next.Sql!, preflight.Errors, startedAt).ConfigureAwait(false);
                        repaired = true;
                    }
                }
                if (next.IsTerminal) return next;
                if (WorkflowExecution.IsCancelled(next, ct)) return WorkflowExecution.Cancelled(next);
                if (repaired)
                {
                    var preflight = await _operations.PreflightSqlAsync(
                        next.Plan!, next.Sql!, next.Context, next.Context.Cancellation).ConfigureAwait(false);
                    if (!preflight.Ok)
                        next = await RepairSqlAsync(next, input, next.Sql!, preflight.Errors, startedAt).ConfigureAwait(false);
                }
                if (next.IsTerminal) return next;
                if (WorkflowExecution.IsCancelled(next, ct)) return WorkflowExecution.Cancelled(next);
                if (next.Context.HasStoredResults)
                    return next;
                page = await _operations.ExecuteSqlAsync(next.Plan!, next.Sql!, next.Context, next.Context.Cancellation).ConfigureAwait(false);
                if (WorkflowExecution.IsCancelled(next, ct)) return WorkflowExecution.Cancelled(next);
                WorkflowExecution.AddStep(next.Context, startedAt, "execute_sql", "ok", page.ResultId);
            }
            foreach (var warning in page.Warnings) WorkflowExecution.AddWarning(next.Context, warning);
            if (page.StoredRowCount == 0) return WorkflowExecution.Terminal(next, "no_data");
            return next;
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(state.Context, startedAt, state.Plan?.Route == AnalysisDataRoute.DashboardMetric ? "dashboard_metric" : "execute_sql", "error", null, exception.Error); return WorkflowExecution.Error(state, exception); }
        catch (Exception) { return WorkflowExecution.Unexpected(state, state.Plan?.Route == AnalysisDataRoute.DashboardMetric ? "dashboard_metric" : "execute_sql", startedAt); }
    }
    private async Task<WorkflowState> RepairSqlAsync(WorkflowState state, SqlGenerationInput input, SqlDraft rejected, HarnessError[] errors, long startedAt)
    {
        if (state.SqlRepairs >= WorkflowLimits.MaxSqlRepairs || state.ModelCalls >= WorkflowLimits.MaxModelCalls)
        {
            WorkflowExecution.AddStep(state.Context, startedAt, "execute_sql", "error", null, errors[0]);
            return WorkflowExecution.Terminal(state, "failed", errors[0]);
        }
        try
        {
            var repaired = await _model.RepairSqlAsync(new SqlRepairInput(input, rejected, errors), state.Context.Cancellation).ConfigureAwait(false);
            if (WorkflowExecution.IsCancelled(state, state.Context.Cancellation)) return WorkflowExecution.Cancelled(state);
            var next = WorkflowExecution.Next(state, sql: repaired, modelCalls: state.ModelCalls + 1, sqlRepairs: state.SqlRepairs + 1);
            var valid = WorkflowContractValidator.ValidateSqlDraft(repaired, state.Plan!);
            if (valid.Ok)
                return next;
            WorkflowExecution.AddStep(next.Context, startedAt, "execute_sql", "error", null, valid.Errors[0]);
            return WorkflowExecution.Terminal(next, "failed", valid.Errors[0]);
        }
        catch (HarnessException exception)
        {
            WorkflowExecution.AddStep(state.Context, startedAt, "execute_sql", "error", null,
                exception.ValidationErrors.FirstOrDefault() ?? exception.Error);
            return WorkflowExecution.Error(state, exception);
        }
    }
    private static Interpretation ContextInterpretation(WorkflowState state) => state.Context.Interpretation ??
        new Interpretation(state.Plan!.MetricId, state.Plan.MetricId, "", null, null, null);
}
