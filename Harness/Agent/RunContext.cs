#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ArmGov.Harness;

public sealed class RunContext
{
    public const int BudgetMs = 120_000;
    public const int MaxModelCalls = 8;
    public const int MaxRepairs = 2;

    private readonly TimeProvider _clock;
    private readonly long _startedAt;

    public RunContext(string runId, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Run id is required.", nameof(runId));

        RunId = runId;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Cancellation = ct;
        Results = new ResultStore(runId);
        AsOf = clock.GetUtcNow();
        Deadline = AsOf.AddMilliseconds(BudgetMs);
        _startedAt = clock.GetTimestamp();
        Candidates = new Dictionary<string, EmployeeCandidate[]>(StringComparer.Ordinal);
        ResolvedEmployees = new Dictionary<string, EmployeeCandidate>(StringComparer.Ordinal);
        ResolvedSelections = new List<ResolvedEmployeeSelection>();
        Steps = new List<AgentStep>();
        Warnings = new List<string>();
        Messages = new List<System.Text.Json.JsonElement>();
    }

    public string RunId { get; }
    public ResultStore Results { get; }
    public Interpretation? Interpretation { get; set; }
    public Dictionary<string, EmployeeCandidate[]> Candidates { get; }
    public Dictionary<string, EmployeeCandidate> ResolvedEmployees { get; }
    public List<ResolvedEmployeeSelection> ResolvedSelections { get; }
    public EntitySelection[] Selections { get; private set; } = Array.Empty<EntitySelection>();
    public DateTimeOffset AsOf { get; }
    public DateTimeOffset Deadline { get; }
    public CancellationToken Cancellation { get; }
    public int ModelCalls { get; set; }
    public int Repairs { get; set; }
    public bool HasStoredResults => Results.All().Length > 0;
    public bool ContextLocked { get; set; }
    public List<AgentStep> Steps { get; }
    public List<string> Warnings { get; }
    public List<System.Text.Json.JsonElement> Messages { get; }

    public void SetSelections(EntitySelection[] selections) =>
        Selections = selections ?? Array.Empty<EntitySelection>();

    public bool IsExpired =>
        _clock.GetUtcNow() >= Deadline || Cancellation.IsCancellationRequested;

    public bool CanRepair => Repairs < MaxRepairs;

    public bool CanCallModel => ModelCalls < MaxModelCalls && !IsExpired;

    public long ElapsedMs => ElapsedMsSince(_startedAt);

    public long Timestamp() => _clock.GetTimestamp();

    public long ElapsedMsSince(long startedAt)
    {
        var elapsed = (long)_clock.GetElapsedTime(startedAt, _clock.GetTimestamp()).TotalMilliseconds;
        return elapsed < 0 ? 0 : elapsed;
    }

    public CancellationToken LinkedToken(CancellationToken outer) =>
        CancellationTokenSource.CreateLinkedTokenSource(Cancellation, outer).Token;

    public void RegisterCandidates(string mention, EmployeeCandidate[] candidates)
    {
        Candidates[mention] = candidates;
    }

    public ResolvedEmployeeSelection RegisterResolved(string mention, EmployeeCandidate employee)
    {
        ResolvedEmployees[mention] = employee;
        var existing = ResolvedSelections.FirstOrDefault(selection =>
            selection.Employee.Id == employee.Id);
        if (existing is not null)
            return existing;

        var resolved = new ResolvedEmployeeSelection(
            mention,
            employee,
            $"selected_employee_{ResolvedSelections.Count + 1}");
        ResolvedSelections.Add(resolved);
        return resolved;
    }

    public bool TryGetKnownCandidate(long id, out EmployeeCandidate candidate)
    {
        foreach (var group in Candidates.Values)
        {
            foreach (var item in group)
            {
                if (item.Id == id)
                {
                    candidate = item;
                    return true;
                }
            }
        }

        foreach (var item in ResolvedEmployees.Values)
        {
            if (item.Id == id)
            {
                candidate = item;
                return true;
            }
        }

        candidate = default!;
        return false;
    }
}

public sealed record ResolvedEmployeeSelection(
    string Mention,
    EmployeeCandidate Employee,
    string ParameterName);
