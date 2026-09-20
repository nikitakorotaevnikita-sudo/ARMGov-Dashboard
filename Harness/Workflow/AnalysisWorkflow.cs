#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows;

namespace ArmGov.Harness;

public sealed class AnalysisWorkflow : IAnalysisRunner
{
    private readonly IAnalysisStageModel _model;
    private readonly IAnalysisOperations _operations;
    private readonly ReportDraftService _reports;
    private readonly AnalyticsCatalog _catalog;
    private readonly TimeProvider _clock;

    public AnalysisWorkflow(IAnalysisStageModel model, IAnalysisOperations operations, ReportDraftService reports, AnalyticsCatalog catalog, TimeProvider clock)
    { _model = model ?? throw new ArgumentNullException(nameof(model)); _operations = operations ?? throw new ArgumentNullException(nameof(operations)); _reports = reports ?? throw new ArgumentNullException(nameof(reports)); _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _clock = clock ?? throw new ArgumentNullException(nameof(clock)); }

    public async Task<AnalysisResponse> RunAsync(AnalysisRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new AnalysisResponse(Guid.NewGuid().ToString("N"), "incomplete", null, [], [], 0,
                ["Выполнение отменено или бюджет времени исчерпан."], null, null);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RunContext.BudgetMs);
        var prepare = new PrepareExecutor(_operations, _clock, budget.Token);
        var plan = new PlanExecutor(_model);
        var resolve = new ResolveEntitiesExecutor(_operations);
        var acquire = new AcquireDataExecutor(_model, _operations, _catalog);
        var report = new DraftReportExecutor(_reports);
        var finish = new FinishExecutor();
        var builder = new WorkflowBuilder(prepare);
        builder.AddEdge(prepare, plan);
        builder.AddEdge(plan, resolve);
        builder.AddEdge(resolve, acquire);
        builder.AddEdge(acquire, report);
        builder.AddEdge(report, finish).WithOutputFrom(finish);
        try
        {
            await using var run = await InProcessExecution.RunAsync(builder.Build(), request, "analysis", budget.Token).ConfigureAwait(false);
            if (budget.IsCancellationRequested)
                return CancelledResponse();
            var completed = run.NewEvents.OfType<ExecutorCompletedEvent>().Single(item => item.ExecutorId == "finish");
            return completed.Data as AnalysisResponse ?? FailedResponse();
        }
        catch (OperationCanceledException)
        {
            return CancelledResponse();
        }
        catch (Exception) { return FailedResponse(); }
    }

    private static AnalysisResponse CancelledResponse() => new(Guid.NewGuid().ToString("N"), "incomplete", null, [], [], 0,
        ["Выполнение отменено или бюджет времени исчерпан."], null, null);

    private static AnalysisResponse FailedResponse() => new(Guid.NewGuid().ToString("N"), "failed", null, [], [], 0,
        [], null, new HarnessError("workflow_failure", "Сервис аналитики временно недоступен.", false));
}
