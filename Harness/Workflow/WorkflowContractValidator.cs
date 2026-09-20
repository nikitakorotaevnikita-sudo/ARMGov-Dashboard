#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArmGov.Harness;

public static class WorkflowContractValidator
{
    private static readonly Regex RelationHintPattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    public static ValidationResult ValidatePlan(
        AnalysisPlan plan,
        DateTimeOffset asOf,
        IReadOnlySet<string> dashboardMetrics)
    {
        var errors = new List<HarnessError>();
        if (plan is null)
            return Invalid("invalid_plan", "План аналитики обязателен.");

        if (string.IsNullOrWhiteSpace(plan.MetricId))
            Add(errors, "invalid_metric_id", "Идентификатор метрики обязателен.");

        ValidatePeriod(plan.Period, asOf, errors);
        ValidateEmployeeMentions(plan.EmployeeMentions, errors);
        ValidateRelationHints(plan.RelationHints, errors);
        ValidateRoute(plan, dashboardMetrics, errors);

        return new ValidationResult(errors.Count == 0, errors.ToArray());
    }

    public static ValidationResult ValidateSqlDraft(SqlDraft draft, AnalysisPlan plan)
    {
        var errors = new List<HarnessError>();
        if (draft is null || plan is null)
            return Invalid("invalid_sql_draft", "Черновик SQL и план обязательны.");

        if (string.IsNullOrWhiteSpace(draft.Sql) ||
            draft.Sql.Length > WorkflowLimits.MaxSqlLength)
        {
            Add(errors, "invalid_sql", "SQL должен содержать от 1 до 20000 символов.");
        }

        if (!string.Equals(draft.MetricId, plan.MetricId, StringComparison.Ordinal))
        {
            Add(errors, "metric_mismatch", "Метрика SQL должна точно совпадать с метрикой плана.");
        }

        return new ValidationResult(errors.Count == 0, errors.ToArray());
    }

    private static void ValidatePeriod(
        PeriodSpec? period,
        DateTimeOffset asOf,
        List<HarnessError> errors)
    {
        if (period is null)
        {
            Add(errors, "invalid_period", "Период обязателен.");
            return;
        }

        try
        {
            ToolDispatcher.ParsePeriod(ToPeriodElement(period), asOf);
        }
        catch (HarnessException exception)
        {
            errors.Add(exception.Error);
        }
        catch (Exception)
        {
            Add(errors, "invalid_period", "Период имеет недопустимый формат.");
        }
    }

    private static JsonElement ToPeriodElement(PeriodSpec period)
    {
        var values = new Dictionary<string, object?>
        {
            ["kind"] = period.Kind
        };
        if (period.Months.HasValue)
            values["months"] = period.Months.Value;
        if (period.From.HasValue)
            values["from"] = period.From.Value;
        if (period.To.HasValue)
            values["to"] = period.To.Value;
        return JsonSerializer.SerializeToElement(values, HarnessJson.Options);
    }

    private static void ValidateEmployeeMentions(
        ImmutableArray<string> employeeMentions,
        List<HarnessError> errors)
    {
        if (employeeMentions.IsDefault)
        {
            Add(errors, "invalid_employee_mentions", "Список упоминаний сотрудников обязателен.");
            return;
        }
        if (employeeMentions.Length > WorkflowLimits.MaxEmployeeMentions)
            Add(errors, "too_many_employee_mentions", "Допускается не более 10 упоминаний сотрудников.");
        if (employeeMentions.Any(string.IsNullOrWhiteSpace))
            Add(errors, "invalid_employee_mention", "Упоминание сотрудника не может быть пустым.");
    }

    private static void ValidateRelationHints(
        ImmutableArray<string> relationHints,
        List<HarnessError> errors)
    {
        if (relationHints.IsDefault)
        {
            Add(errors, "invalid_relation_hints", "Список подсказок отношений обязателен.");
            return;
        }
        if (relationHints.Length > WorkflowLimits.MaxRelationHints)
            Add(errors, "too_many_relation_hints", "Допускается не более 12 подсказок отношений.");
        foreach (var hint in relationHints)
        {
            if (string.IsNullOrWhiteSpace(hint) || !RelationHintPattern.IsMatch(hint))
                Add(errors, "invalid_relation_hint", "Подсказка отношения должна иметь вид schema.table.");
        }
    }

    private static void ValidateRoute(
        AnalysisPlan plan,
        IReadOnlySet<string>? dashboardMetrics,
        List<HarnessError> errors)
    {
        switch (plan.Route)
        {
            case AnalysisDataRoute.DashboardMetric:
                if (string.IsNullOrWhiteSpace(plan.DashboardMetric) ||
                    dashboardMetrics is null ||
                    !dashboardMetrics.Contains(plan.DashboardMetric))
                {
                    Add(errors, "invalid_dashboard_route", "Для dashboard-маршрута нужна известная готовая метрика.");
                }
                if (!plan.RelationHints.IsDefaultOrEmpty)
                {
                    Add(errors, "invalid_dashboard_route", "Dashboard-маршрут не принимает подсказки отношений.");
                }
                break;

            case AnalysisDataRoute.GeneratedSql:
                if (plan.DashboardMetric is not null)
                {
                    Add(errors, "invalid_generated_sql_route", "Generated SQL не принимает имя готовой метрики.");
                }
                break;

            default:
                Add(errors, "invalid_data_route", "Маршрут получения данных неизвестен.");
                break;
        }
    }

    private static void Add(List<HarnessError> errors, string code, string message) =>
        errors.Add(new HarnessError(code, message, false));

    private static ValidationResult Invalid(string code, string message) =>
        new(false, new[] { new HarnessError(code, message, false) });
}
