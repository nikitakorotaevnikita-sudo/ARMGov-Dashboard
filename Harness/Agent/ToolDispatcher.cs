#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class ToolDispatcher
{
    private static readonly Regex SqlPeriodToken = new(
        @"@(?:from|to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly AnalyticsCatalog _catalog;
    private readonly IEmployeeResolver _employees;
    private readonly IQueryExecutor _executor;
    private readonly Func<string, JsonElement, CancellationToken, Task<QueryResult>> _dashboard;

    public ToolDispatcher(
        AnalyticsCatalog catalog,
        IEmployeeResolver employees,
        IQueryExecutor executor,
        Func<string, JsonElement, CancellationToken, Task<QueryResult>> dashboard)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _employees = employees ?? throw new ArgumentNullException(nameof(employees));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
    }

    public Task<EmployeeCandidate?> ResolveEmployeeAsync(long id, CancellationToken ct) =>
        _employees.GetAsync(id, ct);

    public async Task<object> ExecuteAsync(
        ModelAction action,
        RunContext context,
        CancellationToken ct)
    {
        return action.Name switch
        {
            "search_catalog" => SearchCatalog(action),
            "describe_table" => DescribeTable(action),
            "find_employees" => await FindEmployeesAsync(action, context, ct).ConfigureAwait(false),
            "set_context" => SetContext(action, context),
            "dashboard_metric" => await DashboardMetricAsync(action, context, ct).ConfigureAwait(false),
            "execute_sql" => await ExecuteSqlAsync(action, context, ct).ConfigureAwait(false),
            "read_result" => ReadResult(action, context),
            "submit_report" => SubmitReport(action),
            "clarify" => Clarify(action, context),
            _ => throw new HarnessException(new HarnessError(
                "unknown_function",
                "Requested an unknown tool.",
                false))
        };
    }

    private object SearchCatalog(ModelAction action)
    {
        var query = action.Arguments.GetProperty("query").GetString()!;
        return _catalog.Search(query);
    }

    private object DescribeTable(ModelAction action)
    {
        var schema = action.Arguments.GetProperty("schema").GetString()!;
        var table = action.Arguments.GetProperty("table").GetString()!;
        var fields = action.Arguments.GetProperty("fields")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        return _catalog.Describe(schema, table, fields);
    }

    private async Task<object> FindEmployeesAsync(
        ModelAction action,
        RunContext context,
        CancellationToken ct)
    {
        var tokens = action.Arguments.GetProperty("tokens")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        var mention = string.Join(' ', tokens);
        var candidates = await _employees.SearchAsync(tokens, ct).ConfigureAwait(false);
        context.RegisterCandidates(mention, candidates);
        return candidates.Select(candidate => new
        {
            candidate.Id,
            candidate.Name,
            candidate.Department
        }).ToArray();
    }

    private object SetContext(ModelAction action, RunContext context)
    {
        if (context.ContextLocked)
        {
            throw new HarnessException(new HarnessError(
                "context_locked",
                "Контекст нельзя менять после получения данных.",
                false));
        }

        var metricId = action.Arguments.GetProperty("metricId").GetString()!;
        var period = action.Arguments.GetProperty("period");
        var (from, to) = ParsePeriod(period, context.AsOf);
        var interpretation = BuildInterpretation(metricId, from, to);
        context.Interpretation = interpretation;
        return new
        {
            metricId = interpretation.MetricId,
            label = interpretation.Label,
            unit = interpretation.Unit,
            from = interpretation.From,
            to = interpretation.To,
            dateField = interpretation.DateField
        };
    }

    private async Task<object> DashboardMetricAsync(
        ModelAction action,
        RunContext context,
        CancellationToken ct)
    {
        EnsureContext(action.Name, context);
        var name = action.Arguments.GetProperty("name").GetString()!;
        var args = action.Arguments.TryGetProperty("args", out var argsElement) &&
                   argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : JsonSerializer.SerializeToElement(new { }, HarnessJson.Options);
        var data = await _dashboard(name, args, ct).ConfigureAwait(false);
        var stored = context.Results.Add(data);
        context.ContextLocked = true;
        return ToResultPage(context, stored.ResultId);
    }

    private async Task<object> ExecuteSqlAsync(
        ModelAction action,
        RunContext context,
        CancellationToken ct)
    {
        EnsureContext(action.Name, context);
        var sql = action.Arguments.GetProperty("sql").GetString()!;
        var metricId = action.Arguments.GetProperty("metricId").GetString()!;
        if (!string.Equals(metricId, context.Interpretation!.MetricId, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessException(new HarnessError(
                "metric_mismatch",
                "metricId SQL не совпадает с контекстом запуска.",
                true));
        }

        if (context.Interpretation.From is not null || context.Interpretation.To is not null)
        {
            if (!SqlPeriodToken.IsMatch(sql))
            {
                throw new HarnessException(new HarnessError(
                    "missing_period_binding",
                    "SQL с периодом должен ссылаться на @from и @to.",
                    true));
            }
        }

        var parameters = ReadParameters(action.Arguments);
        var query = new QuerySpec(
            sql,
            parameters,
            metricId,
            context.Interpretation.From,
            context.Interpretation.To);
        var data = await _executor.ExecuteAsync(query, ct).ConfigureAwait(false);
        var stored = context.Results.Add(data);
        context.ContextLocked = true;
        return ToResultPage(context, stored.ResultId);
    }

    private object ReadResult(ModelAction action, RunContext context)
    {
        var resultId = action.Arguments.GetProperty("resultId").GetString()!;
        var offset = action.Arguments.GetProperty("offset").GetInt32();
        var take = action.Arguments.GetProperty("take").GetInt32();
        return context.Results.Page(context.RunId, resultId, offset, take);
    }

    private static object SubmitReport(ModelAction action) =>
        JsonSerializer.Deserialize<ReportSpec>(
            action.Arguments.GetProperty("report").GetRawText(),
            HarnessJson.Options)
        ?? throw new HarnessException(new HarnessError(
            "invalid_report",
            "Отчёт не распознан.",
            true));

    private static object Clarify(ModelAction action, RunContext context)
    {
        var question = action.Arguments.GetProperty("question").GetString()!;
        var ids = action.Arguments.GetProperty("candidateIds")
            .EnumerateArray()
            .Select(item => item.GetInt64())
            .ToArray();
        var candidates = new List<EmployeeCandidate>();
        foreach (var id in ids)
        {
            if (!context.TryGetKnownCandidate(id, out var candidate))
            {
                throw new HarnessException(new HarnessError(
                    "unknown_candidate",
                    "Уточнение может ссылаться только на найденных кандидатов.",
                    false));
            }
            candidates.Add(candidate);
        }

        return new Clarification(question, candidates.ToArray());
    }

    private Interpretation BuildInterpretation(
        string metricId,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        if (string.Equals(metricId, "generic_query", StringComparison.OrdinalIgnoreCase))
        {
            return new Interpretation(
                "generic_query",
                "Произвольный запрос",
                "строка результата",
                from,
                to,
                null);
        }

        MetricDefinition metric;
        try
        {
            metric = _catalog.GetMetric(metricId);
        }
        catch (KeyNotFoundException)
        {
            throw new HarnessException(new HarnessError(
                "unknown_metric",
                "Метрика отсутствует в каталоге; сначала найдите её или используйте generic_query.",
                true));
        }

        return new Interpretation(
            metric.Id,
            metric.Definition,
            metric.Unit,
            from,
            to,
            metric.DateField);
    }

    public static (DateTimeOffset? From, DateTimeOffset? To) ParsePeriod(
        JsonElement period,
        DateTimeOffset asOf)
    {
        var kind = period.GetProperty("kind").GetString()!;
        var hasMonths = period.TryGetProperty("months", out var monthsElement);
        var hasFrom = period.TryGetProperty("from", out var fromElement);
        var hasTo = period.TryGetProperty("to", out var toElement);

        switch (kind)
        {
            case "all":
                if (hasMonths || hasFrom || hasTo)
                {
                    throw new HarnessException(new HarnessError(
                        "invalid_period",
                        "kind=all не допускает months/from/to.",
                        false));
                }
                return (null, null);

            case "months":
                if (hasFrom || hasTo)
                {
                    throw new HarnessException(new HarnessError(
                        "invalid_period",
                        "kind=months не допускает from/to.",
                        false));
                }
                if (!hasMonths || !monthsElement.TryGetInt32(out var months) ||
                    months is < 1 or > 1200)
                {
                    throw new HarnessException(new HarnessError(
                        "invalid_period",
                        "months должно быть от 1 до 1200.",
                        false));
                }
                var to = asOf;
                var from = to.AddMonths(-months);
                return (from, to);

            case "range":
                if (hasMonths)
                {
                    throw new HarnessException(new HarnessError(
                        "invalid_period",
                        "kind=range не допускает months.",
                        false));
                }
                if (!hasFrom || !hasTo ||
                    fromElement.ValueKind != JsonValueKind.String ||
                    toElement.ValueKind != JsonValueKind.String)
                {
                    throw new HarnessException(new HarnessError(
                        "invalid_period",
                        "kind=range требует from/to в ISO8601.",
                        false));
                }
                var fromDate = DateTimeOffset.Parse(
                    fromElement.GetString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                var toDate = DateTimeOffset.Parse(
                    toElement.GetString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                if (fromDate >= toDate)
                {
                    throw new HarnessException(new HarnessError(
                        "invalid_period",
                        "from должно быть меньше to.",
                        false));
                }
                return (fromDate, toDate);

            default:
                throw new HarnessException(new HarnessError(
                    "invalid_period",
                    "Неизвестный kind периода.",
                    false));
        }
    }

    private static Dictionary<string, JsonElement> ReadParameters(JsonElement arguments)
    {
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!arguments.TryGetProperty("parameters", out var raw) ||
            raw.ValueKind != JsonValueKind.Object)
        {
            return parameters;
        }

        foreach (var property in raw.EnumerateObject())
            parameters[property.Name] = property.Value.Clone();
        return parameters;
    }

    private static void EnsureContext(string tool, RunContext context)
    {
        if (context.Interpretation is null)
        {
            throw new HarnessException(new HarnessError(
                "missing_context",
                $"Инструмент {tool} требует set_context.",
                true));
        }
    }

    private static ResultPage ToResultPage(RunContext context, string resultId) =>
        // Короткая страница в history: полный объём доступен через read_result.
        context.Results.Page(context.RunId, resultId, offset: 0, take: 5);
}
