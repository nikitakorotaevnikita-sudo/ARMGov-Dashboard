#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public record AnalysisRequest(string Question, EntitySelection[] Selections);
public record EntitySelection(string Mention, long EmployeeId);
public record PeriodSpec(
    string Kind,
    int? Months,
    DateTimeOffset? From,
    DateTimeOffset? To);
public record AnalysisContextSpec(string MetricId, PeriodSpec Period);
public record ColumnSpec(string Name, string Title, string Type);
public record Truncation(bool Rows, bool Columns, string[] Cells, bool Bytes);
public record QuerySpec(
    string Sql,
    Dictionary<string, JsonElement> Parameters,
    string MetricId,
    DateTimeOffset? From,
    DateTimeOffset? To);
public record QueryResult(
    string Source,
    ColumnSpec[] Columns,
    JsonElement[][] Rows,
    string? EffectiveSql,
    DateTimeOffset RetrievedAt,
    Truncation Truncation,
    string[] Warnings);
public record StoredResult(string RunId, string ResultId, QueryResult Data);
public record ResultPage(
    string RunId,
    string ResultId,
    ColumnSpec[] Columns,
    JsonElement[][] Rows,
    int Offset,
    int StoredRowCount,
    bool HasMore,
    Truncation Truncation,
    string[] Warnings);
public record CellRef(string ResultId, int Row, string Column);
public record FactSpec(string Id, string Operation, CellRef[] Inputs);
public record BlockSpec(
    string Kind,
    string ResultId,
    string[] Columns,
    Dictionary<string, JsonElement>? EqualsFilter,
    int? Limit);
public record Interpretation(
    string MetricId,
    string Label,
    string Unit,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? DateField);
public record ReportSpec(
    string Title,
    Interpretation Interpretation,
    BlockSpec[] Blocks,
    FactSpec[] Facts,
    string[] TextTemplates,
    string? Commentary);
public record HarnessError(string Code, string Message, bool Retryable);
public record ValidationResult(bool Ok, HarnessError[] Errors);

public sealed class HarnessException : Exception
{
    public HarnessException(HarnessError error)
        : base(error.Message)
    {
        Error = error;
    }

    public HarnessError Error { get; }
}

public record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters);
public record ModelAction(
    string Name,
    JsonElement Arguments,
    JsonElement AssistantMessage);
public record AgentStep(
    int N,
    string Tool,
    string Status,
    long ElapsedMs,
    string? ResultId,
    HarnessError? Error);
public record Clarification(
    string Question,
    EmployeeCandidate[] Candidates);
public record EmployeeCandidate(long Id, string Name, string Department);
public record RenderedReport(
    string Title,
    Interpretation Interpretation,
    string[] VerifiedText,
    Dictionary<string, JsonElement> Facts,
    BlockSpec[] Blocks,
    string? Commentary);
public record AnalysisResponse(
    string RunId,
    string Status,
    RenderedReport? Report,
    StoredResult[] Datasets,
    AgentStep[] Steps,
    long ElapsedMs,
    string[] Warnings,
    Clarification? Clarification,
    HarnessError? Error);

public interface IModelProvider
{
    Task<ModelAction> NextAsync(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);

    JsonElement Feedback(ModelAction action, object result);
}

public interface IQueryExecutor
{
    Task<QueryResult> ExecuteAsync(QuerySpec query, CancellationToken ct);
}

public interface IEmployeeResolver
{
    Task<EmployeeCandidate[]> SearchAsync(
        string[] tokens,
        CancellationToken ct);

    Task<EmployeeCandidate?> GetAsync(long id, CancellationToken ct);
}

public static class AnalysisRequestValidator
{
    public static ValidationResult Validate(AnalysisRequest request)
    {
        var errors = new List<HarnessError>();

        if (string.IsNullOrWhiteSpace(request.Question) ||
            request.Question.Length > 4000)
        {
            errors.Add(new HarnessError(
                "invalid_question",
                "Вопрос должен содержать от 1 до 4000 символов.",
                false));
        }

        if (request.Selections is null)
        {
            errors.Add(new HarnessError(
                "invalid_selections",
                "Список выбранных сущностей обязателен.",
                false));
        }
        else
        {
            if (request.Selections.Length > 10)
            {
                errors.Add(new HarnessError(
                    "too_many_selections",
                    "Допускается не более 10 выбранных сущностей.",
                    false));
            }

            foreach (var selection in request.Selections)
            {
                if (selection is null ||
                    selection.EmployeeId <= 0 ||
                    string.IsNullOrWhiteSpace(selection.Mention))
                {
                    errors.Add(new HarnessError(
                        "invalid_selection",
                        "У выбранной сущности нужны непустое упоминание и положительный ID.",
                        false));
                }
            }
        }

        return new ValidationResult(errors.Count == 0, errors.ToArray());
    }
}
