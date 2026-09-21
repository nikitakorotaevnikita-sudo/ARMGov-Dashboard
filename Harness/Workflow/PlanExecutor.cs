#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class PlanExecutor : Executor<WorkflowState, WorkflowState>
{
    private readonly IAnalysisStageModel _model;
    private readonly MetricSummary[] _metrics;
    private readonly PlanningRelationSummary[] _relations;
    private readonly IReadOnlySet<string> _dashboardMetrics;

    public PlanExecutor(IAnalysisStageModel model, AnalyticsCatalog catalog) : base("plan")
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(catalog);
        _metrics = catalog.GetPlanningMetricSummaries();
        _relations = catalog.GetPlanningRelationSummaries();
        _dashboardMetrics = _metrics
            .Where(metric => !string.IsNullOrWhiteSpace(metric.DashboardMetric))
            .Select(metric => metric.DashboardMetric!)
            .ToHashSet(StringComparer.Ordinal);
    }
    public override async ValueTask<WorkflowState> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.IsTerminal) return state;
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        if (state.ModelCalls >= WorkflowLimits.MaxModelCalls) return WorkflowExecution.Cancelled(state);
        try
        {
            var plan = await _model.PlanAsync(new PlanningInput(state.Request.Question, state.Context.AsOf,
                state.Request.Selections, _metrics, _relations), state.Context.Cancellation).ConfigureAwait(false);
            if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
            var validation = WorkflowContractValidator.ValidatePlan(plan, state.Context.AsOf, _dashboardMetrics);
            if (!validation.Ok)
                throw new HarnessException(validation.Errors[0]);
            WorkflowExecution.AddStep(state.Context, "plan", "ok");
            return WorkflowExecution.Next(state, plan: plan, modelCalls: state.ModelCalls + 1);
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(state.Context, "plan", "error", null, exception.Error); return WorkflowExecution.Error(state, exception); }
        catch (Exception) { return WorkflowExecution.Unexpected(state, "plan"); }
    }
}
