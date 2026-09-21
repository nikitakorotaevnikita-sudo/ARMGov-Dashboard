#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public interface IAnalysisStageModel
{
    Task<AnalysisPlan> PlanAsync(PlanningInput input, CancellationToken ct);
    Task<SqlDraft> DraftSqlAsync(SqlGenerationInput input, CancellationToken ct);
    Task<SqlDraft> RepairSqlAsync(SqlRepairInput input, CancellationToken ct);
    Task<ReportDraft> DraftReportAsync(ReportGenerationInput input, CancellationToken ct);
    Task<ReportDraft> RepairReportAsync(ReportRepairInput input, CancellationToken ct);
}

public sealed record MetricSummary(
    string MetricId,
    string Definition,
    string? DashboardMetric);

public sealed record PlanningRelationSummary(
    string Name,
    string Title,
    string Description);

public sealed record PlanningInput(
    string Question,
    DateTimeOffset AsOf,
    EntitySelection[] ConfirmedSelections,
    MetricSummary[] Metrics,
    PlanningRelationSummary[] Relations);

public sealed record VerifiedEmployee(
    long Id,
    string Name,
    string ParameterName);

public sealed record CatalogFieldProjection(
    string Name,
    string Type,
    string Description);

public sealed record CatalogRelationProjection(
    string Name,
    string Description,
    CatalogFieldProjection[] Fields);

public sealed record CatalogRelationshipProjection(
    string From,
    string To,
    string Direction,
    string Cardinality);

public sealed record CatalogProjection(
    CatalogRelationProjection[] Relations,
    CatalogRelationshipProjection[] Relationships,
    MetricDefinition Metric);

public sealed record SqlGenerationInput(
    string Question,
    AnalysisPlan Plan,
    Interpretation Interpretation,
    VerifiedEmployee[] Employees,
    CatalogProjection Catalog);

public sealed record SqlRepairInput(
    SqlGenerationInput Original,
    SqlDraft Rejected,
    HarnessError[] Errors);

public sealed record ResultManifest(
    string ResultId,
    ColumnSpec[] Columns,
    JsonElement[][] Rows,
    int RowCount,
    Truncation Truncation);

public sealed record ReportGenerationInput(
    string Question,
    Interpretation Interpretation,
    ResultManifest[] Results);

public sealed record ReportRepairInput(
    ReportGenerationInput Original,
    ReportDraft Rejected,
    HarnessError[] Errors);
