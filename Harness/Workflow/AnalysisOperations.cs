#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class AnalysisOperations : IAnalysisOperations
{
    private readonly ToolDispatcher _dispatcher;

    public AnalysisOperations(ToolDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<PreparationResult> PrepareAsync(
        AnalysisRequest request,
        RunContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        EnsureValid(AnalysisRequestValidator.Validate(request));
        EnsureContextMutable(context);
        context.SetSelections(request.Selections);

        foreach (var selection in request.Selections)
        {
            var employee = await _dispatcher.ResolveEmployeeAsync(selection.EmployeeId, ct)
                .ConfigureAwait(false);
            if (employee is null)
            {
                throw new HarnessException(new HarnessError(
                    "invalid_selection",
                    "Выбранный сотрудник не найден.",
                    false));
            }
            context.RegisterResolved(selection.Mention, employee);
        }

        return new PreparationResult(VerifiedEmployees(context));
    }

    public async Task<EntityResolutionResult> ResolveAsync(
        AnalysisPlan plan,
        RunContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        EnsureContextMutable(context);

        var candidates = new List<EmployeeCandidate>();
        var unmatched = new List<string>();
        foreach (var mention in plan.EmployeeMentions)
        {
            if (context.ResolvedEmployees.ContainsKey(mention))
                continue;

            var tokens = mention.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                throw new HarnessException(new HarnessError(
                    "invalid_employee_mention",
                    "Упоминание сотрудника не может быть пустым.",
                    false));
            }

            await _dispatcher.ExecuteAsync(
                ServerAction("find_employees", new { tokens }),
                context,
                ct).ConfigureAwait(false);

            if (context.Candidates.TryGetValue(mention, out var found) && found.Length != 1)
            {
                if (found.Length == 0)
                    unmatched.Add(mention);
                else
                    candidates.AddRange(found);
            }
        }

        return new EntityResolutionResult(VerifiedEmployees(context), candidates.ToArray(), unmatched.ToArray());
    }

    public async Task<ResultPage> ExecuteDashboardAsync(
        AnalysisPlan plan,
        RunContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        if (plan.Route != AnalysisDataRoute.DashboardMetric ||
            string.IsNullOrWhiteSpace(plan.DashboardMetric))
        {
            throw new HarnessException(new HarnessError(
                "invalid_dashboard_route",
                "План не содержит готовую метрику dashboard.",
                false));
        }

        await ApplyContextAsync(plan, context, ct).ConfigureAwait(false);
        return ResultPageFrom(await _dispatcher.ExecuteAsync(
            ServerAction("dashboard_metric", new { name = plan.DashboardMetric, args = new { } }),
            context,
            ct).ConfigureAwait(false));
    }

    public async Task<ResultPage> ExecuteSqlAsync(
        AnalysisPlan plan,
        SqlDraft draft,
        RunContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);
        if (plan.Route != AnalysisDataRoute.GeneratedSql)
        {
            throw new HarnessException(new HarnessError(
                "invalid_generated_sql_route",
                "План не использует generated SQL.",
                false));
        }

        await ApplyContextAsync(plan, context, ct).ConfigureAwait(false);
        _dispatcher.EnsureSqlAllowed(draft.Sql);
        return ResultPageFrom(await _dispatcher.ExecuteAsync(
            ServerAction("execute_sql", new
            {
                sql = draft.Sql,
                parameters = new Dictionary<string, object?>(),
                metricId = draft.MetricId
            }),
            context,
            ct).ConfigureAwait(false));
    }

    public async Task<ValidationResult> PreflightSqlAsync(
        AnalysisPlan plan,
        SqlDraft draft,
        RunContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);
        if (plan.Route != AnalysisDataRoute.GeneratedSql)
        {
            throw new HarnessException(new HarnessError(
                "invalid_generated_sql_route",
                "План не использует generated SQL.",
                false));
        }

        await ApplyContextAsync(plan, context, ct).ConfigureAwait(false);
        return _dispatcher.CheckSqlBindings(draft.Sql, context);
    }

    private async Task ApplyContextAsync(
        AnalysisPlan plan,
        RunContext context,
        CancellationToken ct)
    {
        await _dispatcher.ExecuteAsync(
            ServerAction("set_context", new
            {
                metricId = plan.MetricId,
                period = PeriodArguments(plan.Period)
            }),
            context,
            ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object> PeriodArguments(PeriodSpec period)
    {
        var values = new Dictionary<string, object>
        {
            ["kind"] = period.Kind
        };
        if (period.Months.HasValue) values["months"] = period.Months.Value;
        if (period.From.HasValue) values["from"] = period.From.Value;
        if (period.To.HasValue) values["to"] = period.To.Value;
        return values;
    }

    private static VerifiedEmployee[] VerifiedEmployees(RunContext context) =>
        context.ResolvedSelections
            .Select(item => new VerifiedEmployee(
                item.Employee.Id,
                item.Employee.Name,
                item.ParameterName))
            .ToArray();

    private static ResultPage ResultPageFrom(object value) => value as ResultPage
        ?? throw new InvalidOperationException("Tool dispatcher returned an unexpected result.");

    private static void EnsureValid(ValidationResult validation)
    {
        if (!validation.Ok)
            throw new HarnessException(validation.Errors[0]);
    }

    private static void EnsureContextMutable(RunContext context)
    {
        if (context.ContextLocked || context.HasStoredResults)
        {
            throw new HarnessException(new HarnessError(
                "context_locked",
                "Контекст нельзя менять после получения данных.",
                false));
        }
    }

    private static ModelAction ServerAction(string name, object arguments) =>
        new(
            name,
            JsonSerializer.SerializeToElement(arguments, HarnessJson.Options),
            JsonSerializer.SerializeToElement(new { role = "server", content = name }));
}
