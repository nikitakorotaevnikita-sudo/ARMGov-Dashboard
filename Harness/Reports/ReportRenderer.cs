#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArmGov.Harness;

public static class ReportRenderer
{
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{([A-Za-z][A-Za-z0-9_]{0,63})\}\}", RegexOptions.CultureInvariant);

    public static RenderedReport Render(
        ReportSpec spec,
        ResultStore store,
        Interpretation interpretation)
    {
        var validation = ReportValidator.Validate(spec, store, interpretation);
        if (!validation.Ok)
            throw new HarnessException(new HarnessError(
                "invalid_report",
                string.Join(" ", validation.Errors.Select(error => error.Message)),
                false));

        var results = store.All().ToDictionary(result => result.ResultId, StringComparer.Ordinal);
        var evaluationErrors = new List<HarnessError>();
        var facts = ReportValidator.EvaluateFacts(spec.Facts, results, evaluationErrors);
        if (evaluationErrors.Count > 0)
            throw new HarnessException(new HarnessError(
                "invalid_report",
                string.Join(" ", evaluationErrors.Select(error => error.Message)),
                false));

        var verified = spec.TextTemplates
            .Select(template => PlaceholderPattern.Replace(
                template,
                match => Display(facts[match.Groups[1].Value])))
            .Select(template => WebUtility.HtmlEncode(template) ?? string.Empty)
            .ToList();

        if (facts.Values.Any(value => value.ValueKind == JsonValueKind.Null))
            verified.Add("Предупреждение: деление на ноль; значение отношения не рассчитано.");
        if (spec.Blocks.Any(block =>
                results.TryGetValue(block.ResultId, out var result) &&
                IsTruncated(result.Data.Truncation)))
            verified.Add("Показана часть доступных данных.");

        return new RenderedReport(
            WebUtility.HtmlEncode(spec.Title) ?? string.Empty,
            interpretation,
            verified.ToArray(),
            facts.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            spec.Blocks.Select(CloneBlock).ToArray(),
            spec.Commentary is null
                ? null
                : $"Комментарий модели, не проверен: {WebUtility.HtmlEncode(spec.Commentary)}");
    }

    private static string Display(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? "—" : value.GetRawText();

    private static BlockSpec CloneBlock(BlockSpec block) =>
        block with
        {
            Columns = block.Columns.ToArray(),
            EqualsFilter = block.EqualsFilter?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal)
        };

    private static bool IsTruncated(Truncation truncation) =>
        truncation.Rows ||
        truncation.Columns ||
        truncation.Bytes ||
        truncation.Cells.Length > 0;
}
