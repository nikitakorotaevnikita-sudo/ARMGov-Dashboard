#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public interface IAnalysisOperations
{
    Task<PreparationResult> PrepareAsync(
        AnalysisRequest request,
        RunContext context,
        CancellationToken ct);

    Task<EntityResolutionResult> ResolveAsync(
        AnalysisPlan plan,
        RunContext context,
        CancellationToken ct);

    Task<ResultPage> ExecuteDashboardAsync(
        AnalysisPlan plan,
        RunContext context,
        CancellationToken ct);

    Task<ResultPage> ExecuteSqlAsync(
        AnalysisPlan plan,
        SqlDraft draft,
        RunContext context,
        CancellationToken ct);
}

public sealed record PreparationResult(VerifiedEmployee[] Employees);

public sealed record EntityResolutionResult(
    VerifiedEmployee[] Employees,
    EmployeeCandidate[] Candidates,
    string[]? UnmatchedMentions = null);
