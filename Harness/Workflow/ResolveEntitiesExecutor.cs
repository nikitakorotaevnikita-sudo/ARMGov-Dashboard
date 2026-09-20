#nullable enable

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

internal sealed class ResolveEntitiesExecutor : Executor<WorkflowState, WorkflowState>
{
    private readonly IAnalysisOperations _operations;
    public ResolveEntitiesExecutor(IAnalysisOperations operations) : base("resolve") => _operations = operations;
    public override async ValueTask<WorkflowState> HandleAsync(WorkflowState state, IWorkflowContext context, CancellationToken ct)
    {
        if (state.IsTerminal) return state;
        if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
        try
        {
            var result = await _operations.ResolveAsync(state.Plan!, state.Context, state.Context.Cancellation).ConfigureAwait(false);
            if (WorkflowExecution.IsCancelled(state, ct)) return WorkflowExecution.Cancelled(state);
            if (result.Candidates.Length > 0 || result.UnmatchedMentions is { Length: > 0 })
            {
                WorkflowExecution.AddStep(state.Context, "resolve_entities", "ok");
                return WorkflowExecution.Terminal(state, "needs_clarification", clarification: new Clarification(
                    result.UnmatchedMentions is { Length: > 0 } ? "Сотрудник не найден. Уточните имя." : "Уточните сотрудника.",
                    result.Candidates));
            }
            WorkflowExecution.AddStep(state.Context, "resolve_entities", "ok");
            return WorkflowExecution.Next(state, employees: ImmutableArray.CreateRange(result.Employees));
        }
        catch (OperationCanceledException) { return WorkflowExecution.Cancelled(state); }
        catch (HarnessException exception) { WorkflowExecution.AddStep(state.Context, "resolve_entities", "error", null, exception.Error); return WorkflowExecution.Error(state, exception); }
        catch (Exception) { return WorkflowExecution.Unexpected(state, "resolve_entities"); }
    }
}
