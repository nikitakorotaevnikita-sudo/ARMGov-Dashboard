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
    private static readonly HashSet<string> DashboardPeriodMetrics = new(StringComparer.Ordinal)
    {
        "execution_discipline", "overview", "process", "stuck", "by_kind", "departments"
    };

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

    internal void EnsureSqlAllowed(string sql)
    {
        var validation = SqlScopePolicy.Check(sql, _catalog.AllowedRelations);
        if (!validation.Ok)
            throw new HarnessException(validation.Errors[0]);
    }

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
        try
        {
            return _catalog.Search(query);
        }
        catch (ArgumentException ex)
        {
            throw new HarnessException(new HarnessError(
                "invalid_search_query",
                ex.Message,
                true));
        }
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
        var resolved = candidates.Length == 1
            ? context.RegisterResolved(mention, candidates[0])
            : null;
        return candidates.Select(candidate => new
        {
            candidate.Id,
            candidate.Name,
            candidate.Department,
            ServerParameter = resolved?.Employee.Id == candidate.Id
                ? "@" + resolved.ParameterName
                : null
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
        if (context.ResolvedSelections.Count > 0)
        {
            throw new HarnessException(new HarnessError(
                "selection_not_supported",
                "Готовая метрика не умеет применять выбранных сотрудников. Используйте execute_sql с серверными параметрами выбора.",
                true));
        }
        EnsureContext(action, context);
        var name = action.Arguments.GetProperty("name").GetString()!;
        MaybeUpgradeGenericContext(name, context);
        var args = action.Arguments.TryGetProperty("args", out var argsElement) &&
                   argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : JsonSerializer.SerializeToElement(new { }, HarnessJson.Options);
        args = EnsureDashboardPeriod(name, args, context);
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
        EnsureContext(action, context);
        var sql = action.Arguments.GetProperty("sql").GetString()!;
        var metricId = action.Arguments.GetProperty("metricId").GetString()!;
        if (!string.Equals(metricId, context.Interpretation!.MetricId, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessException(new HarnessError(
                "metric_mismatch",
                "metricId SQL не совпадает с контекстом запуска.",
                true));
        }

        if (string.Equals(metricId, "personal_instruction_count", StringComparison.OrdinalIgnoreCase) &&
            context.ResolvedSelections.Count == 0)
        {
            throw new HarnessException(new HarnessError(
                "missing_entity_resolution",
                "Персональная метрика требует сначала найти сотрудника через find_employees.",
                true));
        }

        if (context.Interpretation.From is not null || context.Interpretation.To is not null)
        {
            if (!SqlGuard.ReferencesParameter(sql, "from") ||
                !SqlGuard.ReferencesParameter(sql, "to"))
            {
                throw new HarnessException(new HarnessError(
                    "missing_period_binding",
                    "SQL с периодом должен ссылаться на @from и @to.",
                    true));
            }
        }

        var parameters = ReadParameters(action.Arguments);
        foreach (var selection in context.ResolvedSelections)
        {
            if (!SqlGuard.ReferencesParameter(sql, selection.ParameterName))
            {
                throw new HarnessException(new HarnessError(
                    "missing_selection_binding",
                    $"SQL должен фильтровать выбранного сотрудника через @{selection.ParameterName}.",
                    true));
            }
            parameters[selection.ParameterName] =
                JsonSerializer.SerializeToElement(selection.Employee.Id, HarnessJson.Options);
        }
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
        if (!context.HasStoredResults)
        {
            throw new HarnessException(new HarnessError(
                "unknown_result",
                "Результата ещё нет. Сначала вызовите dashboard_metric или execute_sql.",
                true));
        }

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
                    true));
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

    private void EnsureContext(ModelAction action, RunContext context)
    {
        if (context.Interpretation is not null)
            return;

        var metricId = InferMetricId(action, context);
        var period = InferPeriodElement(UserQuestion(context));
        if (string.Equals(action.Name, "dashboard_metric", StringComparison.Ordinal) &&
            period.GetProperty("kind").GetString() == "all" &&
            action.Arguments.TryGetProperty("args", out var dashboardArgs) &&
            dashboardArgs.ValueKind == JsonValueKind.Object &&
            dashboardArgs.TryGetProperty("period", out var dashboardPeriod) &&
            dashboardPeriod.ValueKind == JsonValueKind.String)
        {
            period = DashboardPeriodElement(dashboardPeriod.GetString()!);
        }
        var (from, to) = ParsePeriod(period, context.AsOf);
        context.Interpretation = BuildInterpretation(metricId, from, to);
    }

    private void MaybeUpgradeGenericContext(string dashboardName, RunContext context)
    {
        if (context.ContextLocked ||
            context.Interpretation is null ||
            !string.Equals(context.Interpretation.MetricId, "generic_query", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var mapped = dashboardName switch
        {
            "execution_discipline" or "overview" or "process" => "execution_discipline",
            "appeal_topics" => "appeal_topics",
            "stuck" => "overdue_assignment_kpi",
            _ => "generic_query"
        };
        if (string.Equals(mapped, "generic_query", StringComparison.OrdinalIgnoreCase))
            return;

        context.Interpretation = BuildInterpretation(
            mapped,
            context.Interpretation.From,
            context.Interpretation.To);
    }

    private string InferMetricId(ModelAction action, RunContext context)
    {
        if (string.Equals(action.Name, "execute_sql", StringComparison.Ordinal) &&
            action.Arguments.TryGetProperty("metricId", out var sqlMetric) &&
            sqlMetric.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sqlMetric.GetString()))
        {
            return sqlMetric.GetString()!;
        }

        if (string.Equals(action.Name, "dashboard_metric", StringComparison.Ordinal) &&
            action.Arguments.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() switch
            {
                "execution_discipline" or "overview" or "process" => "execution_discipline",
                "appeal_topics" => "appeal_topics",
                "stuck" => "overdue_assignment_kpi",
                _ => "generic_query"
            };
        }

        var question = UserQuestion(context);
        if (!string.IsNullOrWhiteSpace(question))
        {
            try
            {
                var hits = _catalog.Search(question, 5);
                foreach (var hit in hits.EnumerateArray())
                {
                    if (hit.TryGetProperty("kind", out var kind) &&
                        kind.GetString() == "metric" &&
                        hit.TryGetProperty("id", out var id) &&
                        id.GetString() is { Length: > 0 } metricId)
                    {
                        return metricId;
                    }
                }
            }
            catch (ArgumentException)
            {
                // пустой или слишком длинный вопрос — generic_query
            }
        }

        return "generic_query";
    }

    internal static JsonElement InferPeriodElement(string question)
    {
        var text = question ?? "";
        if (Regex.IsMatch(text, @"12\s*мес", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(text, @"за\s+год", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return JsonDocument.Parse("""{"kind":"months","months":12}""").RootElement.Clone();
        }

        if (text.Contains("квартал", StringComparison.OrdinalIgnoreCase))
            return JsonDocument.Parse("""{"kind":"months","months":3}""").RootElement.Clone();

        if (Regex.IsMatch(text, @"за\s+(последний\s+)?месяц", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return JsonDocument.Parse("""{"kind":"months","months":1}""").RootElement.Clone();

        return JsonDocument.Parse("""{"kind":"all"}""").RootElement.Clone();
    }

    private static JsonElement EnsureDashboardPeriod(
        string name,
        JsonElement args,
        RunContext context)
    {
        var supportsPeriod = DashboardPeriodMetrics.Contains(name);
        var bounded = context.Interpretation?.From is not null || context.Interpretation?.To is not null;
        var expected = ExactDashboardPeriod(context);

        if (bounded && !supportsPeriod)
        {
            throw new HarnessException(new HarnessError(
                "unsupported_dashboard_period",
                $"Готовая метрика {name} не поддерживает период контекста; используйте execute_sql.",
                true));
        }
        if (bounded && expected is null)
        {
            throw new HarnessException(new HarnessError(
                "unsupported_dashboard_period",
                "Готовые метрики поддерживают только скользящие периоды 1, 3 или 12 месяцев; используйте execute_sql.",
                true));
        }

        string? actual = null;
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("period", out var actualElement) &&
            actualElement.ValueKind == JsonValueKind.String)
        {
            actual = actualElement.GetString();
        }
        if (actual is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new HarnessException(new HarnessError(
                "period_mismatch",
                $"Период dashboard_metric ({actual}) не совпадает с периодом контекста ({expected ?? "all"}).",
                true));
        }
        if (expected is null || actual is not null)
            return args;

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in args.EnumerateObject())
                map[property.Name] = property.Value.Clone();
        }

        map["period"] = JsonSerializer.SerializeToElement(expected);
        return JsonSerializer.SerializeToElement(map);
    }

    private static string? ExactDashboardPeriod(RunContext context)
    {
        if (context.Interpretation?.From is not DateTimeOffset from ||
            context.Interpretation.To is not DateTimeOffset to ||
            to != context.AsOf)
            return null;
        if (from == context.AsOf.AddMonths(-1)) return "month";
        if (from == context.AsOf.AddMonths(-3)) return "quarter";
        if (from == context.AsOf.AddMonths(-12)) return "year";
        return null;
    }

    private static JsonElement DashboardPeriodElement(string period) => period switch
    {
        "month" => JsonDocument.Parse("""{"kind":"months","months":1}""").RootElement.Clone(),
        "quarter" => JsonDocument.Parse("""{"kind":"months","months":3}""").RootElement.Clone(),
        "year" => JsonDocument.Parse("""{"kind":"months","months":12}""").RootElement.Clone(),
        _ => JsonDocument.Parse("""{"kind":"all"}""").RootElement.Clone()
    };

    private static string UserQuestion(RunContext context)
    {
        if (context.Messages.Count == 0)
            return "";
        var message = context.Messages[0];
        return message.TryGetProperty("content", out var content) &&
               content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";
    }

    private static ResultPage ToResultPage(RunContext context, string resultId) =>
        context.Results.Page(context.RunId, resultId, offset: 0, take: 5);
}
