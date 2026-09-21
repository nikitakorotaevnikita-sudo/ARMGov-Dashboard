#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class PlanExecutor : Executor<WorkflowState, WorkflowState>
{
    private readonly IAnalysisStageModel _model;
    private readonly MetricSummary[] _metrics;
    private readonly PlanningRelationSummary[] _relations;

    public PlanExecutor(IAnalysisStageModel model, AnalyticsCatalog catalog) : base("plan")
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(catalog);
        _metrics = catalog.GetPlanningMetricSummaries();
        _relations = catalog.GetPlanningRelationSummaries();
    }
    public override async ValueTask<WorkflowState> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.IsTerminal) return state;
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        if (state.ModelCalls >= WorkflowLimits.MaxModelCalls) return WorkflowExecution.Cancelled(state);
        var startedAt = WorkflowExecution.Start(state.Context);
        try
        {
            var plan = await _model.PlanAsync(new PlanningInput(state.Request.Question, state.Context.AsOf,
                state.Request.Selections, _metrics, _relations), state.Context.Cancellation).ConfigureAwait(false);
            if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
            var validation = WorkflowContractValidator.ValidatePlan(plan, state.Context.AsOf, _metrics, _relations);
            if (!validation.Ok)
                throw new HarnessException(validation.Errors[0]);
            WorkflowExecution.AddStep(state.Context, startedAt, "plan", "ok");
            return WorkflowExecution.Next(state, plan: plan, modelCalls: state.ModelCalls + 1);
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(state.Context, startedAt, "plan", "error", null, exception.Error); return WorkflowExecution.Error(state, exception); }
        catch (Exception) { return WorkflowExecution.Unexpected(state, "plan", startedAt); }
    }
}
