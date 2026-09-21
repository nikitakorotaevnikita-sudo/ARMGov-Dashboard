#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed class QwenAnalysisStageModel : IAnalysisStageModel
{
    private readonly IQwenJsonClient _client;

    public QwenAnalysisStageModel(IQwenJsonClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<AnalysisPlan> PlanAsync(PlanningInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = await _client.CompleteAsync<AnalysisPlan>(
            WorkflowPrompts.Planning, input, 800, ct).ConfigureAwait(false);
        EnsureValid(WorkflowContractValidator.ValidatePlan(
            plan,
            input.AsOf,
            input.Metrics,
            input.Relations));
        return plan;
    }

    public async Task<SqlDraft> DraftSqlAsync(SqlGenerationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var draft = await _client.CompleteAsync<SqlDraft>(
            WorkflowPrompts.SqlGeneration, ProjectSqlInput(input), 1400, ct).ConfigureAwait(false);
        EnsureValid(WorkflowContractValidator.ValidateSqlDraft(draft, input.Plan));
        return draft;
    }

    public async Task<SqlDraft> RepairSqlAsync(SqlRepairInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var draft = await _client.CompleteAsync<SqlDraft>(
            WorkflowPrompts.SqlRepair, input with
            {
                Original = ProjectSqlInput(input.Original)
            }, 1400, ct).ConfigureAwait(false);
        EnsureValid(WorkflowContractValidator.ValidateSqlDraft(draft, input.Original.Plan));
        return draft;
    }

    public Task<ReportDraft> DraftReportAsync(ReportGenerationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _client.CompleteAsync<ReportDraft>(
            WorkflowPrompts.ReportGeneration, input, 1600, ct);
    }

    public Task<ReportDraft> RepairReportAsync(ReportRepairInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _client.CompleteAsync<ReportDraft>(
            WorkflowPrompts.ReportRepair, input, 1600, ct);
    }

    private static SqlGenerationInput ProjectSqlInput(SqlGenerationInput input)
    {
        var allowed = input.Plan.RelationHints.ToHashSet(StringComparer.Ordinal);
        var relations = (input.Catalog.Relations ?? Array.Empty<CatalogRelationProjection>())
            .Where(relation => allowed.Contains(relation.Name))
            .ToArray();
        var retainedRelationNames = relations
            .Select(relation => relation.Name)
            .ToHashSet(StringComparer.Ordinal);
        var relationships = (input.Catalog.Relationships ?? Array.Empty<CatalogRelationshipProjection>())
            .Where(relationship =>
                retainedRelationNames.Contains(RelationName(relationship.From)) &&
                retainedRelationNames.Contains(RelationName(relationship.To)))
            .ToArray();
        return input with
        {
            Catalog = input.Catalog with
            {
                Relations = relations,
                Relationships = relationships
            }
        };
    }

    private static string RelationName(string fieldReference)
    {
        var separator = fieldReference.LastIndexOf('.');
        return separator > 0 ? fieldReference[..separator] : string.Empty;
    }

    private static void EnsureValid(ValidationResult validation)
    {
        if (validation.Ok)
            return;

        var error = validation.Errors.FirstOrDefault() ?? new HarnessError(
            "invalid_workflow_contract",
            "Ответ модели не прошёл проверку контракта.",
            false);
        throw new HarnessException(error);
    }
}
