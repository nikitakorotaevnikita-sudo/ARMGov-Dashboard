#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class FinishExecutor : Executor<WorkflowState, AnalysisResponse>
{
    public FinishExecutor() : base("finish") { }
    public override ValueTask<AnalysisResponse> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.Terminal is not null) return ValueTask.FromResult(state.Terminal);
        var response = new AnalysisResponse(state.Context.RunId, "completed", state.Report is null ? null :
            ReportRenderer.Render(state.Report, state.Context.Results, state.Context.Interpretation!),
            state.Context.Results.All(), state.Context.Steps.ToArray(), state.Context.ElapsedMs,
            state.Context.Warnings.ToArray(), null, null);
        return ValueTask.FromResult(response);
    }
}
