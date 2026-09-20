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
        "invalid_period",
        "invalid_search_query",
        "unknown_candidate",
        "provider_payload_too_large",
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
        "unverified_numeric_text",
        "invalid_text_templates",
        "invalid_shares",
        "invalid_kpi",
        "invalid_line",
        "invalid_bars",
        "decimal_overflow",
        "metric_mismatch",
        "missing_period_binding",
        "missing_selection_binding",
        "missing_entity_resolution",
        "selection_not_supported",
        "period_mismatch",
        "unsupported_dashboard_period"
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
        AppendResolvedSelections(context);

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
                    ToolDefinitions.Available(context.HasStoredResults),
                    context.LinkedToken(ct)).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (IsRepairable(ex.Error) && context.CanRepair)
            {
                context.Repairs++;
                if (string.Equals(ex.Error.Code, "unstructured_response", StringComparison.Ordinal) ||
                    string.Equals(ex.Error.Code, "provider_payload_too_large", StringComparison.Ordinal))
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
                    report = EnrichReport(report, context);
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
                "Отчёт требует установленного контекста. Сначала вызовите set_context или dashboard_metric.",
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

    /// <summary>
    /// GigaChat часто присылает пустые blocks/facts и цифры в тексте.
    /// Достраиваем блоки из resultId и убираем непроверенные числа из title/templates.
    /// </summary>
    private static ReportSpec EnrichReport(ReportSpec report, RunContext context)
    {
        var stored = context.Results.All();
        var fallbackTitle = StripDigits(context.Interpretation?.Label);
        if (string.IsNullOrWhiteSpace(fallbackTitle))
            fallbackTitle = "Анализ";
        var title = StripDigits(report.Title);
        if (string.IsNullOrWhiteSpace(title))
            title = fallbackTitle;

        var blocks = report.Blocks is { Length: > 0 }
            ? report.Blocks
            : stored.Select(result => new BlockSpec(
                "table",
                result.ResultId,
                result.Data.Columns.Select(column => column.Name).Take(5).ToArray(),
                null,
                Math.Min(12, Math.Max(1, result.Data.Rows.Length)))).ToArray();

        var facts = report.Facts ?? Array.Empty<FactSpec>();
        var templates = (report.TextTemplates ?? Array.Empty<string>())
            .Select(StripDigits)
            .Where(text => !string.IsNullOrWhiteSpace(text) &&
                           !text.Contains("{{", StringComparison.Ordinal))
            .ToArray();
        if (templates.Length == 0)
            templates = ["См. таблицу ниже. Числа только в блоках по данным запросов."];

        var commentary = StripDigits(report.Commentary);
        if (string.IsNullOrWhiteSpace(commentary))
            commentary = null;

        return report with
        {
            Title = title,
            Blocks = blocks,
            Facts = facts,
            TextTemplates = templates,
            Commentary = commentary
        };
    }

    private static string StripDigits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var chars = text.Where(ch => !char.IsDigit(ch)).ToArray();
        return new string(chars).Trim();
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
        // GigaChat требует: каждый role=function сразу после assistant.function_call.
        // Протокольные ошибки чиним user-nudges, без фейкового function-результата.
        var ids = string.Join(", ", context.Results.All().Select(result => result.ResultId));
        var resultHint = ids.Length == 0
            ? ""
            : " Доступные resultId: " + ids +
              ". В submit_report.blocks укажи kind, resultId и columns из результата.";
        context.Messages.Add(JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content =
                "Предыдущий ответ отклонён (" + error.Code + ": " + error.Message + "). " +
                "Вызови ровно одну доступную функцию корректными аргументами." + resultHint
        }, HarnessJson.Options));
    }

    private static void AppendUnstructuredNudge(RunContext context, HarnessError error)
    {
        var ids = string.Join(", ", context.Results.All().Select(result => result.ResultId));
        var resultHint = ids.Length == 0
            ? ""
            : " Доступные resultId: " + ids + ".";
        context.Messages.Add(JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content =
                "Предыдущий ответ отклонён (" + error.Message + "). " +
                "Вызови ровно одну доступную функцию." + resultHint
        }, HarnessJson.Options));
    }

    private static bool IsRepairable(HarnessError error) =>
        RepairableCodes.Contains(error.Code);

    private static void AppendResolvedSelections(RunContext context)
    {
        if (context.ResolvedSelections.Count == 0)
            return;

        var payload = JsonSerializer.Serialize(new
        {
            type = "verified_employee_selections",
            instruction =
                "Для каждого выбранного сотрудника фильтруй SQL только по указанному serverParameter. " +
                "Значения параметров привязывает сервер; не добавляй их в execute_sql.parameters.",
            employees = context.ResolvedSelections.Select(selection => new
            {
                mention = selection.Mention,
                employeeId = selection.Employee.Id,
                name = selection.Employee.Name,
                department = selection.Employee.Department,
                serverParameter = "@" + selection.ParameterName
            })
        }, HarnessJson.Options);
        context.Messages.Add(JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = payload
        }, HarnessJson.Options));
    }

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
