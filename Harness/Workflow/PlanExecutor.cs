#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class PlanExecutor : Executor<WorkflowState, WorkflowState>
{
    private readonly IAnalysisStageModel _model;
    public PlanExecutor(IAnalysisStageModel model) : base("plan") => _model = model;
    public override async ValueTask<WorkflowState> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.IsTerminal) return state;
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        if (state.ModelCalls >= WorkflowLimits.MaxModelCalls) return WorkflowExecution.Cancelled(state);
        try
        {
            var plan = await _model.PlanAsync(new PlanningInput(state.Request.Question, state.Context.AsOf,
                state.Request.Selections, Metrics), ct).ConfigureAwait(false);
            var validation = WorkflowContractValidator.ValidatePlan(plan, state.Context.AsOf, DashboardMetrics);
            if (!validation.Ok)
                throw new HarnessException(validation.Errors[0]);
            WorkflowExecution.AddStep(state.Context, "plan", "ok");
            return WorkflowExecution.Next(state, plan: plan, modelCalls: state.ModelCalls + 1);
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(state.Context, "plan", "error", null, exception.Error); return WorkflowExecution.Error(state, exception); }
    }
    private static readonly MetricSummary[] Metrics =
    [new("execution_discipline", "Тренд исполнительской дисциплины.", "execution_discipline"),
     new("generic_query", "Произвольный SELECT-анализ.", null)];

    private static readonly IReadOnlySet<string> DashboardMetrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "overview", "process", "leaders", "leader_tasks", "stuck", "by_kind", "departments",
        "my_tasks", "appeal_topics", "execution_discipline"
    };
}
