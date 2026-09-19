#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ArmGov.Harness;

public sealed class ResultStore
{
    private readonly string _runId;
    private readonly int _byteLimit;
    private readonly Dictionary<string, StoredResult> _results = new(StringComparer.Ordinal);
    private int _nextId = 1;
    private int _storedBytes;

    public ResultStore(string runId, int byteLimit = 4194304)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw Error("invalid_run_id", "Идентификатор запуска обязателен.");
        if (byteLimit <= 0)
            throw Error("invalid_run_result_limit", "Бюджет результатов должен быть положительным.");

        _runId = runId;
        _byteLimit = byteLimit;
    }

    public StoredResult Add(QueryResult data)
    {
        var limited = ResultLimiter.Limit(data);
        var bytes = ResultLimiter.Size(limited);
        if (bytes > _byteLimit - _storedBytes)
            throw Error(
                "run_result_budget_exceeded",
                "Общий бюджет результатов запуска исчерпан.");

        var resultId = $"r{_nextId}";
        var stored = new StoredResult(_runId, resultId, ResultLimiter.Clone(limited));
        _results.Add(resultId, stored);
        _storedBytes += bytes;
        _nextId++;
        return Clone(stored);
    }

    public StoredResult Get(string runId, string resultId)
    {
        return Clone(Find(runId, resultId));
    }

    public ResultPage Page(
        string runId,
        string resultId,
        int offset = 0,
        int take = 20)
    {
        if (offset < 0 || take is < 1 or > 20)
            throw Error(
                "invalid_result_page",
                "Смещение должно быть неотрицательным, размер страницы — от 1 до 20.");

        var stored = Find(runId, resultId);
        var data = stored.Data;
        var rows = data.Rows.Skip(offset).Take(take)
            .Select(row => row.Select(CloneElement).ToArray())
            .ToArray();
        return new ResultPage(
            _runId,
            resultId,
            data.Columns.Select(column => column with { }).ToArray(),
            rows,
            offset,
            data.Rows.Length,
            offset + rows.Length < data.Rows.Length,
            new Truncation(
                data.Truncation.Rows,
                data.Truncation.Columns,
                data.Truncation.Cells.ToArray(),
                data.Truncation.Bytes),
            data.Warnings.ToArray());
    }

    public StoredResult[] All()
    {
        return _results.Values.Select(Clone).ToArray();
    }

    private StoredResult Find(string runId, string resultId)
    {
        if (!string.Equals(runId, _runId, StringComparison.Ordinal) ||
            !_results.TryGetValue(resultId, out var result))
        {
            throw new HarnessException(new HarnessError(
                "unknown_result",
                "Источник этого запуска не найден",
                true));
        }
        return result;
    }

    private static StoredResult Clone(StoredResult result)
    {
        return result with { Data = ResultLimiter.Clone(result.Data) };
    }

    private static System.Text.Json.JsonElement CloneElement(
        System.Text.Json.JsonElement element)
    {
        using var document = System.Text.Json.JsonDocument.Parse(element.GetRawText());
        return document.RootElement.Clone();
    }

    private static HarnessException Error(string code, string message)
    {
        return new HarnessException(new HarnessError(code, message, false));
    }
}
