#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArmGov.Harness;

public enum AnalysisDataRoute
{
    DashboardMetric,
    GeneratedSql
}

public sealed record AnalysisPlan(
    string MetricId,
    PeriodSpec Period,
    AnalysisDataRoute Route,
    string? DashboardMetric,
    ImmutableArray<string> EmployeeMentions,
    ImmutableArray<string> RelationHints)
{
    public static AnalysisPlan Snapshot(
        string metricId,
        PeriodSpec period,
        AnalysisDataRoute route,
        string? dashboardMetric,
        string[] employeeMentions,
        string[] relationHints) =>
        new(
            metricId,
            period,
            route,
            dashboardMetric,
            Copy(employeeMentions),
            Copy(relationHints));

    private static ImmutableArray<string> Copy(string[] values) =>
        values is null ? default : ImmutableArray.CreateRange(values);
}

public sealed record SqlDraft(string Sql, string MetricId);

public sealed record ReportDraft
{
    [JsonConstructor]
    public ReportDraft(ImmutableReportSpec report)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public ReportDraft(ReportSpec report)
        : this(ImmutableReportSpec.FromReportSpec(report))
    {
    }

    public ImmutableReportSpec Report { get; }

    public ReportSpec ToReportSpec() => Report.ToReportSpec();
}

public sealed record ImmutableReportSpec(
    string Title,
    ImmutableInterpretation Interpretation,
    ImmutableArray<ImmutableBlockSpec> Blocks,
    ImmutableArray<ImmutableFactSpec> Facts,
    ImmutableArray<string> TextTemplates,
    string? Commentary)
{
    public static ImmutableReportSpec FromReportSpec(ReportSpec report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ImmutableReportSpec(
            report.Title,
            ImmutableInterpretation.FromInterpretation(report.Interpretation),
            Copy(report.Blocks, ImmutableBlockSpec.FromBlockSpec),
            Copy(report.Facts, ImmutableFactSpec.FromFactSpec),
            Copy(report.TextTemplates),
            report.Commentary);
    }

    public ReportSpec ToReportSpec() => new(
        Title,
        Interpretation.ToInterpretation(),
        ToArray(Blocks, static block => block.ToBlockSpec()),
        ToArray(Facts, static fact => fact.ToFactSpec()),
        TextTemplates.IsDefault ? null! : TextTemplates.ToArray(),
        Commentary);

    private static ImmutableArray<TTarget> Copy<TSource, TTarget>(
        TSource[]? values,
        Func<TSource, TTarget> convert) =>
        values is null ? default : values.Select(convert).ToImmutableArray();

    private static ImmutableArray<string> Copy(string[]? values) =>
        values is null ? default : ImmutableArray.CreateRange(values);

    private static TTarget[] ToArray<TSource, TTarget>(
        ImmutableArray<TSource> values,
        Func<TSource, TTarget> convert) =>
        values.IsDefault ? null! : values.Select(convert).ToArray();
}

public sealed record ImmutableInterpretation(
    string MetricId,
    string Label,
    string Unit,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? DateField)
{
    public static ImmutableInterpretation FromInterpretation(Interpretation interpretation)
    {
        ArgumentNullException.ThrowIfNull(interpretation);
        return new ImmutableInterpretation(
            interpretation.MetricId,
            interpretation.Label,
            interpretation.Unit,
            interpretation.From,
            interpretation.To,
            interpretation.DateField);
    }

    public Interpretation ToInterpretation() =>
        new(MetricId, Label, Unit, From, To, DateField);
}

public sealed record ImmutableBlockSpec(
    string Kind,
    string ResultId,
    ImmutableArray<string> Columns,
    ImmutableDictionary<string, JsonElement>? EqualsFilter,
    int? Limit)
{
    public static ImmutableBlockSpec FromBlockSpec(BlockSpec block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new ImmutableBlockSpec(
            block.Kind,
            block.ResultId,
            block.Columns is null ? default : ImmutableArray.CreateRange(block.Columns),
            block.EqualsFilter?.ToImmutableDictionary(StringComparer.Ordinal),
            block.Limit);
    }

    public BlockSpec ToBlockSpec() => new(
        Kind,
        ResultId,
        Columns.IsDefault ? null! : Columns.ToArray(),
        EqualsFilter is null
            ? null
            : new Dictionary<string, JsonElement>(EqualsFilter, StringComparer.Ordinal),
        Limit);
}

public sealed record ImmutableFactSpec(
    string Id,
    string Operation,
    ImmutableArray<ImmutableCellRef> Inputs)
{
    public static ImmutableFactSpec FromFactSpec(FactSpec fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return new ImmutableFactSpec(
            fact.Id,
            fact.Operation,
            fact.Inputs is null
                ? default
                : fact.Inputs.Select(ImmutableCellRef.FromCellRef).ToImmutableArray());
    }

    public FactSpec ToFactSpec() => new(
        Id,
        Operation,
        Inputs.IsDefault
            ? null!
            : Inputs.Select(input => input.ToCellRef()).ToArray());
}

public sealed record ImmutableCellRef(string ResultId, int Row, string Column)
{
    public static ImmutableCellRef FromCellRef(CellRef cell) =>
        new(cell.ResultId, cell.Row, cell.Column);

    public CellRef ToCellRef() => new(ResultId, Row, Column);
}

internal sealed record WorkflowState(
    AnalysisRequest Request,
    RunContext Context,
    AnalysisPlan? Plan,
    SqlDraft? Sql,
    ReportSpec? Report,
    AnalysisResponse? Terminal,
    int ModelCalls,
    int SqlRepairs,
    int ReportRepairs)
{
    public bool IsTerminal => Terminal is not null;
}
