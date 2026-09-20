#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class PrepareExecutor : Executor<AnalysisRequest, WorkflowState>
{
    private readonly IAnalysisOperations _operations;
    private readonly TimeProvider _clock;

    public PrepareExecutor(IAnalysisOperations operations, TimeProvider clock) : base("prepare")
    { _operations = operations; _clock = clock; }

    public override async ValueTask<WorkflowState> HandleAsync(AnalysisRequest request, IWorkflowContext context, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RunContext.BudgetMs);
        var run = new RunContext(Guid.NewGuid().ToString("N"), _clock, budget.Token);
        var state = new WorkflowState(request, run, null, null, null, null, 0, 0, 0);
        try
        {
            if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
            await _operations.PrepareAsync(request, run, budget.Token).ConfigureAwait(false);
            WorkflowExecution.AddStep(run, "prepare", "ok");
            return state;
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(run, "prepare", "error", null, exception.Error); return WorkflowExecution.Terminal(state, "failed", exception.Error); }
    }
}
