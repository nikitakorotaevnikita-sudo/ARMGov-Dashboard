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
    private readonly CancellationToken _runCancellation;

    public PrepareExecutor(IAnalysisOperations operations, TimeProvider clock, CancellationToken runCancellation) : base("prepare")
    { _operations = operations; _clock = clock; _runCancellation = runCancellation; }

    public override async ValueTask<WorkflowState> HandleAsync(AnalysisRequest request, IWorkflowContext context, CancellationToken ct)
    {
        var run = new RunContext(Guid.NewGuid().ToString("N"), _clock, _runCancellation);
        var state = new WorkflowState(request, run, null, null, null, null, 0, 0, 0);
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        var startedAt = WorkflowExecution.Start(run);
        try
        {
            await _operations.PrepareAsync(request, run, _runCancellation).ConfigureAwait(false);
            if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
            WorkflowExecution.AddStep(run, startedAt, "prepare", "ok");
            return state;
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(run, startedAt, "prepare", "error", null, exception.Error); return WorkflowExecution.Terminal(state, "failed", exception.Error); }
        catch (Exception) { return WorkflowExecution.Unexpected(state, "prepare", startedAt); }
    }
}
