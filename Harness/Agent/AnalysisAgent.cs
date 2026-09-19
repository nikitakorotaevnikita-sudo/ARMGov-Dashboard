#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class AnalysisAgent
{
    private static readonly HashSet<string> RepairableCodes = new(StringComparer.Ordinal)
    {
        "provider_protocol_error",
        "invalid_function_arguments",
        "unknown_function",
        "unstructured_response",
        "missing_context",
        "unknown_metric",
        "invalid_report",
        "unknown_result",
        "unknown_column",
        "invalid_fact_id",
        "altered_interpretation",
        "invalid_block_kind",
        "invalid_block_columns",
        "invalid_filter",
        "invalid_fact_operation",
        "invalid_fact_inputs",
        "truncated_fact",
        "numeric_literal",
        "unknown_placeholder",
        "duplicate_fact_id",
        "invalid_text_templates",
        "invalid_shares",
        "invalid_kpi",
        "invalid_line",
        "invalid_bars",
        "decimal_overflow",
        "metric_mismatch",
        "missing_period_binding"
    };

    private readonly IModelProvider _provider;
    private readonly ToolDispatcher _dispatcher;
    private readonly TimeProvider _clock;

    public AnalysisAgent(
        IModelProvider provider,
        ToolDispatcher dispatcher,
        TimeProvider clock)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<AnalysisResponse> RunAsync(
        AnalysisRequest request,
        CancellationToken ct)
    {
        var validation = AnalysisRequestValidator.Validate(request);
        if (!validation.Ok)
        {
            return Failed(
                Guid.NewGuid().ToString("N"),
                validation.Errors[0],
                Array.Empty<AgentStep>(),
                0,
                Array.Empty<string>());
        }

        var runId = Guid.NewGuid().ToString("N");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RunContext.BudgetMs);
        var context = new RunContext(runId, _clock, budget.Token);
        context.SetSelections(request.Selections);
        context.Messages.Add(UserMessage(request.Question));

        foreach (var selection in request.Selections)
        {
            var employee = await _dispatcher.ResolveEmployeeAsync(
                    selection.EmployeeId,
                    budget.Token)
                .ConfigureAwait(false);
            if (employee is null)
            {
                return Failed(
                    runId,
                    new HarnessError(
                        "invalid_selection",
                        $"Сотрудник {selection.EmployeeId} не найден.",
                        false),
                    context.Steps.ToArray(),
                    context.ElapsedMs,
                    context.Warnings.ToArray());
            }
            context.RegisterResolved(selection.Mention, employee);
        }

        while (true)
        {
            if (context.IsExpired)
                return Incomplete(context, "Бюджет времени исчерпан.");

            if (!context.CanCallModel)
                return Incomplete(context, "Достигнут лимит обращений к модели.");

            ModelAction action;
            var modelStarted = _clock.GetTimestamp();
            try
            {
                context.ModelCalls++;
                action = await _provider.NextAsync(
                    context.Messages,
                    ToolDefinitions.All,
                    context.LinkedToken(ct)).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (IsRepairable(ex.Error) && context.CanRepair)
            {
                context.Repairs++;
                if (string.Equals(ex.Error.Code, "unstructured_response", StringComparison.Ordinal))
                    AppendUnstructuredNudge(context, ex.Error);
                else
                    AppendProtocolFeedback(context, ex.Error);
                continue;
            }
            catch (HarnessException ex)
            {
                return context.HasStoredResults
                    ? Incomplete(context, ex.Error.Message)
                    : Failed(
                        runId,
                        ex.Error,
                        context.Steps.ToArray(),
                        context.ElapsedMs,
                        context.Warnings.ToArray());
            }

            var toolValidation = ToolDefinitions.Validate(action.Name, action.Arguments);
            if (!toolValidation.Ok)
            {
                var error = toolValidation.Errors[0];
                RecordStep(context.Steps.Count + 1, context, action, modelStarted, null, error, "error");
                if (IsRepairable(error) && context.CanRepair)
                {
                    context.Repairs++;
                    AppendToolFeedback(context, action, error);
                    continue;
                }

                return context.HasStoredResults
                    ? Incomplete(context, error.Message)
                    : Incomplete(context, error.Message);
            }

            var stepIndex = context.Steps.Count + 1;
            string? resultId = null;
            HarnessError? stepError = null;
            var status = "ok";
            object? payload = null;

            try
            {
                payload = await _dispatcher.ExecuteAsync(
                    action,
                    context,
                    context.LinkedToken(ct)).ConfigureAwait(false);

                if (payload is ReportSpec report)
                {
                    var reportResult = TryCompleteReport(context, report);
                    if (reportResult.Completed)
                    {
                        RecordStep(stepIndex, context, action, modelStarted, null, null, "ok");
                        return reportResult.Response!;
                    }
                    stepError = reportResult.Error;
                    status = "error";
                    RecordStep(stepIndex, context, action, modelStarted, null, stepError, status);
                    if (reportResult.Error is not null &&
                        IsRepairable(reportResult.Error) &&
                        context.CanRepair)
                    {
                        context.Repairs++;
                        AppendToolFeedback(context, action, reportResult.Error);
                        continue;
                    }
                    return Incomplete(context, reportResult.Error?.Message ?? "Отчёт не принят.");
                }

                if (payload is Clarification clarification)
                {
                    RecordStep(stepIndex, context, action, modelStarted, null, null, status);
                    AppendSuccessFeedback(context, action, payload);
                    return new AnalysisResponse(
                        runId,
                        "needs_clarification",
                        null,
                        context.Results.All(),
                        context.Steps.ToArray(),
                        context.ElapsedMs,
                        context.Warnings.ToArray(),
                        clarification,
                        null);
                }

                if (payload is ResultPage page)
                {
                    resultId = page.ResultId;
                    if (page.StoredRowCount == 0)
                    {
                        RecordStep(stepIndex, context, action, modelStarted, resultId, null, status);
                        AppendSuccessFeedback(context, action, payload);
                        return new AnalysisResponse(
                            runId,
                            "no_data",
                            null,
                            context.Results.All(),
                            context.Steps.ToArray(),
                            context.ElapsedMs,
                            context.Warnings.ToArray(),
                            null,
                            null);
                    }
                }

                RecordStep(stepIndex, context, action, modelStarted, resultId, null, status);
                AppendSuccessFeedback(context, action, payload!);
            }
            catch (HarnessException ex)
            {
                stepError = ex.Error;
                status = "error";
                RecordStep(stepIndex, context, action, modelStarted, null, stepError, status);
                AppendToolFeedback(context, action, ex.Error);
                if (IsRepairable(ex.Error) && context.CanRepair)
                {
                    context.Repairs++;
                    continue;
                }

                return context.HasStoredResults
                    ? Incomplete(context, ex.Error.Message)
                    : Incomplete(context, ex.Error.Message);
            }
        }
    }

    private (bool Completed, AnalysisResponse? Response, HarnessError? Error) TryCompleteReport(
        RunContext context,
        ReportSpec report)
    {
        if (context.Interpretation is null)
        {
            return (false, null, new HarnessError(
                "missing_context",
                "Отчёт требует установленного контекста.",
                true));
        }

        var spec = report with { Interpretation = context.Interpretation };
        var validation = ReportValidator.Validate(spec, context.Results, context.Interpretation);
        if (!validation.Ok)
            return (false, null, validation.Errors[0]);

        var rendered = ReportRenderer.Render(spec, context.Results, context.Interpretation);
        return (true, new AnalysisResponse(
            context.RunId,
            "completed",
            rendered,
            context.Results.All(),
            context.Steps.ToArray(),
            context.ElapsedMs,
            context.Warnings.ToArray(),
            null,
            null), null);
    }

    private void RecordStep(
        int stepIndex,
        RunContext context,
        ModelAction action,
        long startedAt,
        string? resultId,
        HarnessError? error,
        string status)
    {
        context.Steps.Add(new AgentStep(
            stepIndex,
            action.Name,
            status,
            (long)_clock.GetElapsedTime(startedAt, _clock.GetTimestamp()).TotalMilliseconds,
            resultId,
            error));
    }

    private void AppendSuccessFeedback(RunContext context, ModelAction action, object payload)
    {
        context.Messages.Add(action.AssistantMessage);
        context.Messages.Add(_provider.Feedback(action, payload));
    }

    private void AppendToolFeedback(RunContext context, ModelAction action, HarnessError error)
    {
        context.Messages.Add(action.AssistantMessage);
        context.Messages.Add(_provider.Feedback(action, new
        {
            error = new
            {
                code = error.Code,
                message = error.Message,
                retryable = error.Retryable
            }
        }));
    }

    private void AppendProtocolFeedback(RunContext context, HarnessError error)
    {
        context.Messages.Add(_provider.Feedback(
            new ModelAction(
                "protocol",
                JsonSerializer.SerializeToElement(new { }, HarnessJson.Options),
                JsonSerializer.SerializeToElement(new
                {
                    role = "assistant",
                    content = ""
                }, HarnessJson.Options)),
            new
            {
                error = new
                {
                    code = error.Code,
                    message = error.Message,
                    retryable = error.Retryable
                }
            }));
    }

    private static void AppendUnstructuredNudge(RunContext context, HarnessError error)
    {
        context.Messages.Add(JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content =
                "Предыдущий ответ без function_call отклонён (" + error.Message + "). " +
                "Вызови ровно одну доступную функцию. Текст без function_call недопустим."
        }, HarnessJson.Options));
    }

    private static bool IsRepairable(HarnessError error) =>
        RepairableCodes.Contains(error.Code);

    private AnalysisResponse Incomplete(RunContext context, string warning)
    {
        if (!context.Warnings.Contains(warning))
            context.Warnings.Add(warning);
        return new AnalysisResponse(
            context.RunId,
            "incomplete",
            null,
            context.Results.All(),
            context.Steps.ToArray(),
            context.ElapsedMs,
            context.Warnings.ToArray(),
            null,
            null);
    }

    private static AnalysisResponse Failed(
        string runId,
        HarnessError error,
        AgentStep[] steps,
        long elapsedMs,
        string[] warnings) =>
        new(
            runId,
            "failed",
            null,
            Array.Empty<StoredResult>(),
            steps,
            elapsedMs,
            warnings,
            null,
            error);

    private static JsonElement UserMessage(string question) =>
        JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = question
        }, HarnessJson.Options);
}
