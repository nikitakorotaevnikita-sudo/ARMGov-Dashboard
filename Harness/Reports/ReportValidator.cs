#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArmGov.Harness;

public static class ReportValidator
{
    private static readonly HashSet<string> BlockKinds =
        new(StringComparer.Ordinal) { "table", "bars", "line", "shares", "kpi" };
    private static readonly Regex FactIdPattern =
        new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{([A-Za-z][A-Za-z0-9_]{0,63})\}\}", RegexOptions.CultureInvariant);

    public static ValidationResult Validate(
        ReportSpec spec,
        ResultStore store,
        Interpretation interpretation)
    {
        var errors = new List<HarnessError>();
        if (spec is null || store is null || interpretation is null)
            return Invalid("invalid_report", "Отчёт и контекст обязательны.");

        if (spec.Interpretation != interpretation)
            Add(errors, "altered_interpretation", "Смысл отчёта не совпадает с контекстом запуска.");

        var results = store.All().ToDictionary(result => result.ResultId, StringComparer.Ordinal);
        ValidateBlocks(spec.Blocks, results, errors);
        var facts = EvaluateFacts(spec.Facts, results, errors);
        ValidateText(spec, facts.Keys, errors);
        return new ValidationResult(errors.Count == 0, errors.ToArray());
    }

    internal static Dictionary<string, JsonElement> EvaluateFacts(
        FactSpec[]? specs,
        IReadOnlyDictionary<string, StoredResult> results,
        List<HarnessError> errors)
    {
        var facts = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (specs is null)
        {
            Add(errors, "invalid_facts", "Список фактов обязателен.");
            return facts;
        }

        foreach (var fact in specs)
        {
            if (fact is null || string.IsNullOrWhiteSpace(fact.Id) ||
                !FactIdPattern.IsMatch(fact.Id))
            {
                Add(errors, "invalid_fact_id", "Идентификатор факта имеет недопустимый формат.");
                continue;
            }
            if (facts.ContainsKey(fact.Id))
            {
                Add(errors, "duplicate_fact_id", $"Факт '{fact.Id}' указан повторно.");
                continue;
            }

            var expected = fact.Operation switch
            {
                "cell" => (Min: 1, Max: 1),
                "sum" => (Min: 1, Max: int.MaxValue),
                "difference" => (Min: 2, Max: 2),
                "ratio" => (Min: 2, Max: 2),
                _ => (Min: -1, Max: -1)
            };
            if (expected.Min < 0)
            {
                Add(errors, "invalid_fact_operation", $"Операция факта '{fact.Id}' неизвестна.");
                continue;
            }
            if (fact.Inputs is null ||
                fact.Inputs.Length < expected.Min ||
                fact.Inputs.Length > expected.Max)
            {
                Add(errors, "invalid_fact_inputs", $"У факта '{fact.Id}' неверное число операндов.");
                continue;
            }

            var values = new List<decimal>(fact.Inputs.Length);
            foreach (var input in fact.Inputs)
            {
                if (!TryReadCell(input, results, errors, out var value))
                    continue;
                values.Add(value);
            }
            if (values.Count != fact.Inputs.Length)
                continue;

            try
            {
                decimal? value = fact.Operation switch
                {
                    "cell" => values[0],
                    "sum" => values.Aggregate(0m, checkedAdd),
                    "difference" => checked(values[0] - values[1]),
                    "ratio" when values[1] == 0m => null,
                    "ratio" => checked(values[0] / values[1]),
                    _ => null
                };
                facts.Add(
                    fact.Id,
                    value.HasValue
                        ? JsonSerializer.SerializeToElement(value.Value)
                        : JsonSerializer.SerializeToElement<decimal?>(null));
            }
            catch (OverflowException)
            {
                Add(errors, "fact_overflow", $"Вычисление факта '{fact.Id}' вышло за диапазон decimal.");
            }
        }

        return facts;

        static decimal checkedAdd(decimal left, decimal right) => checked(left + right);
    }

    private static void ValidateBlocks(
        BlockSpec[]? blocks,
        IReadOnlyDictionary<string, StoredResult> results,
        List<HarnessError> errors)
    {
        if (blocks is null)
        {
            Add(errors, "invalid_blocks", "Список блоков обязателен.");
            return;
        }

        foreach (var block in blocks)
        {
            if (block is null || !BlockKinds.Contains(block.Kind))
            {
                Add(errors, "invalid_block_kind", "Тип блока отчёта неизвестен.");
                continue;
            }
            if (!TryFindResult(block.ResultId, results, errors, "блока", out var stored))
            {
                continue;
            }
            if (block.Limit is <= 0)
                Add(errors, "invalid_block_limit", "Лимит блока должен быть положительным.");

            var sourceColumns = BuildColumns(stored, errors);
            var selected = block.Columns ?? Array.Empty<string>();
            if (selected.Length == 0 ||
                selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            {
                Add(errors, "invalid_block_columns", "Колонки блока обязательны и не должны повторяться.");
                continue;
            }
            if (selected.Any(name => !sourceColumns.ContainsKey(name)))
            {
                Add(errors, "unknown_column", "Блок ссылается на неизвестную колонку.");
                continue;
            }
            if (!ValidateFilter(block.EqualsFilter, sourceColumns, errors))
                continue;

            var rows = FilterRows(stored.Data, block.EqualsFilter, sourceColumns);
            ValidateBlockShape(block, selected.Select(name => sourceColumns[name]).ToArray(), rows, errors);
            ValidateCompleteness(block, stored.Data.Truncation, errors);
        }
    }

    private static Dictionary<string, (ColumnSpec Spec, int Index)> BuildColumns(
        StoredResult stored,
        List<HarnessError> errors)
    {
        var columns = new Dictionary<string, (ColumnSpec, int)>(StringComparer.Ordinal);
        for (var index = 0; index < stored.Data.Columns.Length; index++)
        {
            var column = stored.Data.Columns[index];
            if (!columns.TryAdd(column.Name, (column, index)))
                Add(errors, "duplicate_source_column", $"Источник содержит повтор колонки '{column.Name}'.");
        }
        return columns;
    }

    private static bool ValidateFilter(
        Dictionary<string, JsonElement>? filter,
        IReadOnlyDictionary<string, (ColumnSpec Spec, int Index)> columns,
        List<HarnessError> errors)
    {
        if (filter is null)
            return true;

        var valid = true;
        foreach (var pair in filter)
        {
            if (!columns.TryGetValue(pair.Key, out var column))
            {
                Add(errors, "unknown_filter_column", $"Фильтр ссылается на неизвестную колонку '{pair.Key}'.");
                valid = false;
                continue;
            }
            if (!MatchesDeclaredType(pair.Value, column.Spec.Type, allowNull: true))
            {
                Add(errors, "invalid_filter_value", $"Значение фильтра '{pair.Key}' не совпадает с типом колонки.");
                valid = false;
            }
        }
        return valid;
    }

    private static JsonElement[][] FilterRows(
        QueryResult data,
        Dictionary<string, JsonElement>? filter,
        IReadOnlyDictionary<string, (ColumnSpec Spec, int Index)> columns)
    {
        if (filter is null || filter.Count == 0)
            return data.Rows;

        return data.Rows.Where(row => filter.All(pair =>
        {
            var column = columns[pair.Key];
            return column.Index < row.Length &&
                   EqualValues(row[column.Index], pair.Value, column.Spec.Type);
        })).ToArray();
    }

    private static void ValidateBlockShape(
        BlockSpec block,
        (ColumnSpec Spec, int Index)[] columns,
        JsonElement[][] rows,
        List<HarnessError> errors)
    {
        bool TypeAt(int index, string type) =>
            index < columns.Length &&
            string.Equals(columns[index].Spec.Type, type, StringComparison.Ordinal);
        bool NumbersAfterFirst() =>
            columns.Skip(1).All(column =>
                string.Equals(column.Spec.Type, "number", StringComparison.Ordinal));

        switch (block.Kind)
        {
            case "bars":
                if (columns.Length < 2 ||
                    !(TypeAt(0, "string") || TypeAt(0, "date")) ||
                    !NumbersAfterFirst())
                    Add(errors, "invalid_bars_columns", "Bars требует подпись и числовые значения.");
                break;
            case "line":
                if (columns.Length < 2 || !TypeAt(0, "date") || !NumbersAfterFirst())
                    Add(errors, "invalid_line_columns", "Line требует дату и числовые значения.");
                break;
            case "shares":
                if (columns.Length < 2 || !TypeAt(0, "string") || !NumbersAfterFirst())
                {
                    Add(errors, "invalid_shares_columns", "Shares требует подпись и числовые значения.");
                    break;
                }
                foreach (var row in rows)
                {
                    foreach (var column in columns.Skip(1))
                    {
                        if (column.Index >= row.Length ||
                            !TryDecimal(row[column.Index], out var value) ||
                            value < 0m)
                            Add(errors, "invalid_share_value", "Доли должны быть конечными неотрицательными числами.");
                    }
                }
                break;
            case "kpi":
                if (columns.Length != 1 || !TypeAt(0, "number") || rows.Length != 1)
                    Add(errors, "invalid_kpi", "KPI требует одну числовую колонку и ровно одну строку.");
                break;
        }
    }

    private static void ValidateCompleteness(
        BlockSpec block,
        Truncation truncation,
        List<HarnessError> errors)
    {
        if (!IsTruncated(truncation))
            return;

        var allowedPartial =
            block.Kind == "table" ||
            (block.Kind == "bars" && block.Limit.HasValue);
        if (!allowedPartial)
            Add(errors, "incomplete_dataset", $"Блок '{block.Kind}' требует полный набор данных.");
    }

    private static bool TryReadCell(
        CellRef input,
        IReadOnlyDictionary<string, StoredResult> results,
        List<HarnessError> errors,
        out decimal value)
    {
        value = default;
        if (input is null)
        {
            return false;
        }
        if (!TryFindResult(input.ResultId, results, errors, "факта", out var stored))
            return false;
        var columns = stored.Data.Columns
            .Select((column, index) => (column, index))
            .Where(item => string.Equals(item.column.Name, input.Column, StringComparison.Ordinal))
            .ToArray();
        if (columns.Length != 1)
        {
            Add(errors, "unknown_column", "Факт ссылается на отсутствующую или неоднозначную колонку.");
            return false;
        }
        if (input.Row < 0 || input.Row >= stored.Data.Rows.Length)
        {
            Add(errors, "row_out_of_range", "Факт ссылается на строку вне сохранённого результата.");
            return false;
        }
        var column = columns[0];
        var row = stored.Data.Rows[input.Row];
        if (column.index >= row.Length ||
            !string.Equals(column.column.Type, "number", StringComparison.Ordinal) ||
            !TryDecimal(row[column.index], out value))
        {
            Add(errors, "invalid_fact_value", "Операнд факта должен быть числом decimal.");
            return false;
        }
        if (IsTruncated(stored.Data.Truncation) ||
            stored.Data.Truncation.Cells.Contains(
                $"{input.Row}:{input.Column}",
                StringComparer.Ordinal))
        {
            Add(errors, "truncated_fact", "Факт нельзя вычислять по усечённым данным.");
            return false;
        }
        return true;
    }

    private static bool TryFindResult(
        string? resultId,
        IReadOnlyDictionary<string, StoredResult> results,
        List<HarnessError> errors,
        string location,
        out StoredResult stored)
    {
        stored = null!;
        if (string.IsNullOrWhiteSpace(resultId) ||
            resultId.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
        {
            Add(errors, "invalid_result_id", $"Идентификатор источника {location} должен быть сохранённым resultId.");
            return false;
        }
        if (!results.TryGetValue(resultId, out var found))
        {
            Add(errors, "unknown_result", $"Источник {location} не найден в этом запуске.");
            return false;
        }
        stored = found;
        return true;
    }

    private static void ValidateText(
        ReportSpec spec,
        IEnumerable<string> factIds,
        List<HarnessError> errors)
    {
        var knownFacts = new HashSet<string>(factIds, StringComparer.Ordinal);
        if (spec.TextTemplates is null)
        {
            Add(errors, "invalid_text_templates", "Список текстовых шаблонов обязателен.");
            return;
        }

        foreach (var text in spec.TextTemplates)
        {
            if (text is null)
            {
                Add(errors, "invalid_text_template", "Текстовый шаблон не может быть null.");
                continue;
            }
            foreach (Match match in PlaceholderPattern.Matches(text))
            {
                if (!knownFacts.Contains(match.Groups[1].Value))
                    Add(errors, "unknown_fact_placeholder", "Шаблон ссылается на неизвестный факт.");
            }
            var withoutPlaceholders = PlaceholderPattern.Replace(text, string.Empty);
            if (withoutPlaceholders.Any(char.IsDigit) ||
                withoutPlaceholders.Contains("{{", StringComparison.Ordinal) ||
                withoutPlaceholders.Contains("}}", StringComparison.Ordinal))
                Add(errors, "unverified_numeric_text", "Числа разрешены только через известные ссылки на факты.");
        }

        ValidateUnverifiedDigits(spec.Title, "заголовке", errors);
        ValidateUnverifiedDigits(spec.Commentary, "комментарии", errors);
    }

    private static void ValidateUnverifiedDigits(
        string? text,
        string location,
        List<HarnessError> errors)
    {
        if (text is not null && text.Any(char.IsDigit))
            Add(errors, "unverified_numeric_text", $"Неподтверждённые числа запрещены в {location}.");
    }

    private static bool MatchesDeclaredType(JsonElement value, string type, bool allowNull)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return allowNull;
        return type switch
        {
            "string" or "date" => value.ValueKind == JsonValueKind.String,
            "number" => TryDecimal(value, out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
    }

    private static bool EqualValues(JsonElement left, JsonElement right, string type)
    {
        if (left.ValueKind == JsonValueKind.Null || right.ValueKind == JsonValueKind.Null)
            return left.ValueKind == right.ValueKind;
        return type switch
        {
            "number" => TryDecimal(left, out var a) &&
                        TryDecimal(right, out var b) &&
                        a == b,
            "string" or "date" =>
                left.ValueKind == JsonValueKind.String &&
                right.ValueKind == JsonValueKind.String &&
                string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            "boolean" =>
                left.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                right.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                left.GetBoolean() == right.GetBoolean(),
            _ => false
        };
    }

    private static bool TryDecimal(JsonElement value, out decimal number)
    {
        number = default;
        return value.ValueKind == JsonValueKind.Number &&
               decimal.TryParse(
                   value.GetRawText(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number);
    }

    private static bool IsTruncated(Truncation truncation) =>
        truncation.Rows ||
        truncation.Columns ||
        truncation.Bytes ||
        truncation.Cells.Length > 0;

    private static void Add(List<HarnessError> errors, string code, string message) =>
        errors.Add(new HarnessError(code, message, false));

    private static ValidationResult Invalid(string code, string message) =>
        new(false, new[] { new HarnessError(code, message, false) });
}
