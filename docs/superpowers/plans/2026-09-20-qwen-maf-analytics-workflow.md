# Qwen Microsoft Agent Framework Analytics Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the free-form analytics tool loop with a deterministic Microsoft Agent Framework workflow for `Qwen/Qwen3.8-27B` while preserving the existing read-only SQL and evidence-backed report guarantees.

**Architecture:** Microsoft Agent Framework Workflows runs an in-process sequence of typed executors. A project-owned Qwen JSON client keeps the current Ario HTTP contract and produces typed plan, SQL, and report drafts; server code resolves entities, validates and executes SQL, stores results, and validates every report reference.

**Tech Stack:** .NET 10, `HttpListener`, Microsoft Agent Framework Workflows 1.21.0, Qwen OpenAI-compatible Chat Completions, Npgsql, vanilla JavaScript, Python/Node smoke tests.

**Spec:** `docs/superpowers/specs/2026-09-20-qwen-maf-analytics-workflow-design.md`

## Global Constraints

- The target model is exactly `Qwen/Qwen3.8-27B` through the configured OpenAI-compatible Ario endpoint.
- Database access remains SELECT-only and every generated query passes `SqlGuard`, `SqlScopePolicy`, a read-only transaction, `statement_timeout=10s`, and the existing result limits.
- LLM and database secrets remain only in ignored `config.json` or the existing environment variables and never appear in browser responses, fixtures, traces, or commits.
- `/api/ai/analysis`, `AnalysisRequest`, `AnalysisResponse`, the local-only boundary, and the current Canvas remain backward compatible.
- A run has a 120-second budget, at most one SQL repair and one report repair, 200 rows, 60 columns, 200 characters per cell, 1 MiB per result, and 4 MiB per run.
- GigaChat is not a target of the new workflow. Existing legacy code remains available only for rollback during this change.
- `Microsoft.Agents.AI.Workflows` is pinned to `1.21.0`; the NuGet lock file is committed and self-contained `win-x64` publish must pass before cutover.

## Review Focus

- A malformed, fenced, duplicated, or prose-wrapped model response must consume at most one stage repair and must never reach SQL execution as partially parsed data; pinned in Task 4.
- Ambiguous or missing employee names must finish as `needs_clarification` or an explicit failure before any query executor call; pinned in Tasks 6 and 8.
- A model-generated query that omits a selected employee parameter or the requested period must be rejected before database access; pinned in Task 6.
- A valid SQL result followed by an invented `resultId`, column, fact, or number must not produce `completed`; pinned in Task 7.
- Empty, truncated, timed-out, cancelled, and provider-failed runs must retain distinct public statuses and warnings; pinned in Task 8.

---

### Task 1: Establish the runner seam and freeze the HTTP contract

**Files:**
- Create: `Harness/Agent/IAnalysisRunner.cs`
- Modify: `Harness/Agent/AnalysisAgent.cs:12`
- Modify: `Program.Harness.cs:54-121`
- Modify: `Harness/Hosting/HarnessHost.cs:12-54`
- Test: `tools/harness-tests/HostingTests.cs`
- Test: `tools/harness-tests/AgentTests.cs`

**Interfaces:**
- Produces: `Task<AnalysisResponse> IAnalysisRunner.RunAsync(AnalysisRequest request, CancellationToken ct)`.
- Consumes: existing `AnalysisRequest`, `AnalysisResponse`, and `AnalysisAgent.RunAsync` behavior.

- [ ] **Step 1: Add a failing test that injects an analysis runner without constructing a model provider**

Add a `RecordingAnalysisRunner` to `HostingTests.cs` and assert that `HarnessHost.RunAnalysisAsync` returns its sentinel `runId` while keeping the public response DTO unchanged.

```csharp
public static async Task HostUsesInjectedAnalysisRunner()
{
    var runner = new RecordingAnalysisRunner();
    HarnessHost.TestAnalysisRunnerFactory = () => runner;
    try
    {
        var response = await HarnessHost.RunAnalysisAsync(
            new AnalysisRequest("Покажи просрочку", []),
            CancellationToken.None);

        Check.Equal("runner-test", response.RunId);
        Check.Equal(1, runner.CallCount);
    }
    finally
    {
        HarnessHost.TestAnalysisRunnerFactory = null;
    }
}
```

- [ ] **Step 2: Run the focused tests and verify the new seam is missing**

Run:

```powershell
dotnet run --project tools/harness-tests -c Release -- hosting
```

Expected: FAIL to compile because `TestAnalysisRunnerFactory` and `IAnalysisRunner` do not exist.

- [ ] **Step 3: Add the runner interface and make the legacy agent implement it**

Create `Harness/Agent/IAnalysisRunner.cs`:

```csharp
#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public interface IAnalysisRunner
{
    Task<AnalysisResponse> RunAsync(AnalysisRequest request, CancellationToken ct);
}
```

Change the declaration to `public sealed class AnalysisAgent : IAnalysisRunner`. Add `Program.TestAnalysisRunnerFactory`, expose it through `HarnessHost`, and make `RunAnalysisAsync` invoke the injected runner before both the production factory and the production LLM-configuration check. Rename `CreateAnalysisAgent` to `CreateAnalysisRunner` and return `IAnalysisRunner`.

- [ ] **Step 4: Add a serialization contract assertion for the existing response**

In `HostingTests.cs`, serialize a completed `AnalysisResponse` and assert the camelCase keys remain `runId`, `status`, `report`, `datasets`, `steps`, `elapsedMs`, `warnings`, `clarification`, and `error`. This guards against framework types leaking into the HTTP contract.

- [ ] **Step 5: Run existing agent and hosting suites**

Run:

```powershell
dotnet run --project tools/harness-tests -c Release -- agent
dotnet run --project tools/harness-tests -c Release -- hosting
```

Expected: both suites end with `FAIL=0`.

- [ ] **Step 6: Commit the runner seam**

```powershell
git add Harness/Agent/IAnalysisRunner.cs Harness/Agent/AnalysisAgent.cs Program.Harness.cs Harness/Hosting/HarnessHost.cs tools/harness-tests/HostingTests.cs tools/harness-tests/AgentTests.cs
git commit -m "refactor: add analytics runner seam"
```

### Task 2: Prove Agent Framework compatibility and lock dependencies

**Files:**
- Modify: `armgov-standalone.csproj`
- Create: `packages.lock.json` (generated by restore)
- Create: `tools/harness-tests/WorkflowRuntimeTests.cs`

**Interfaces:**
- Produces: a verified in-process `WorkflowBuilder`/`InProcessExecution` runtime usable by Task 8.
- Consumes: .NET 10 build and the existing self-contained `win-x64` deployment method.

- [ ] **Step 1: Add the runtime smoke test before adding the package**

Create `WorkflowRuntimeTests.cs`:

```csharp
using Microsoft.Agents.AI.Workflows;

public static class WorkflowRuntimeTests
{
    public static async Task SequentialWorkflowReturnsTerminalExecutorData()
    {
        var upper = ((Func<string, string>)(value => value.ToUpperInvariant()))
            .BindAsExecutor("upper");
        var suffix = ((Func<string, string>)(value => value + "-OK"))
            .BindAsExecutor("suffix");
        var builder = new WorkflowBuilder(upper);
        builder.AddEdge(upper, suffix).WithOutputFrom(suffix);

        await using Run run = await InProcessExecution.RunAsync(
            builder.Build(),
            "qwen");

        var completed = run.NewEvents
            .OfType<ExecutorCompletedEvent>()
            .Single(item => item.ExecutorId == "suffix");
        Check.Equal("QWEN-OK", completed.Data as string);
    }
}
```

- [ ] **Step 2: Run the workflow suite and verify the package is absent**

Run:

```powershell
dotnet run --project tools/harness-tests -c Release -- workflowruntime
```

Expected: FAIL to compile because `Microsoft.Agents.AI.Workflows` is unresolved.

- [ ] **Step 3: Pin Agent Framework and enable the NuGet lock file**

Add to the main project:

```xml
<PropertyGroup>
  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.21.0" />
</ItemGroup>
```

Run `dotnet restore --force-evaluate` once to generate `packages.lock.json`. Do not add `Microsoft.Agents.AI.OpenAI`; the Qwen transport remains project-owned.

- [ ] **Step 4: Run the runtime test, Release build, and locked restore**

Run:

```powershell
dotnet restore --locked-mode
dotnet run --project tools/harness-tests -c Release -- workflowruntime
dotnet build -c Release --no-restore
```

Expected: every command exits 0 and the workflow test ends with `FAIL=0`.

- [ ] **Step 5: Verify the shipping form before business migration**

Run:

```powershell
dotnet restore --locked-mode
dotnet publish -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\maf-publish
$published = Start-Process -FilePath .\artifacts\maf-publish\armgov-standalone.exe -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
if ($published.HasExited -and $published.ExitCode -ne 0) { throw "published executable exited with code $($published.ExitCode)" }
if (-not $published.HasExited) { Stop-Process -Id $published.Id }
```

Expected: publish exits 0, the executable starts without an assembly-load error, and any normal application usage/help output is acceptable. Stop the process if it starts the server. If this gate fails, fix packaging in this task; do not begin the workflow migration with an unshippable dependency graph.

- [ ] **Step 6: Commit dependency proof**

```powershell
git add armgov-standalone.csproj packages.lock.json tools/harness-tests/WorkflowRuntimeTests.cs
git commit -m "build: add locked agent workflow runtime"
```

### Task 3: Define typed workflow state and validate model-owned fields

**Files:**
- Create: `Harness/Workflow/WorkflowContracts.cs`
- Create: `Harness/Workflow/WorkflowContractValidator.cs`
- Create: `Harness/Workflow/WorkflowLimits.cs`
- Test: `tools/harness-tests/WorkflowContractTests.cs`

**Interfaces:**
- Produces: `AnalysisDataRoute`, `AnalysisPlan`, `SqlDraft`, `ReportDraft`, `WorkflowState`, and `WorkflowContractValidator`.
- Consumes: `PeriodSpec`, `Interpretation`, `RunContext`, `HarnessError`, and `AnalysisResponse`.

- [ ] **Step 1: Write failing validation tests for routes, periods, and field limits**

Cover these exact cases in `WorkflowContractTests.cs`:

```csharp
public static void DashboardRouteRequiresKnownDashboardMetric()
{
    var dashboardMetrics = new HashSet<string>(StringComparer.Ordinal)
        { "execution_discipline" };
    var plan = new AnalysisPlan(
        "execution_discipline",
        new PeriodSpec("months", 12, null, null),
        AnalysisDataRoute.DashboardMetric,
        null,
        [],
        []);
    var validation = WorkflowContractValidator.ValidatePlan(
        plan,
        DateTimeOffset.Parse("2026-09-20T12:00:00+04:00"),
        dashboardMetrics);
    Check.True(!validation.Ok);
    Check.Equal("invalid_dashboard_route", validation.Errors[0].Code);
}

public static void GeneratedSqlRejectsDashboardMetricName()
{
    var dashboardMetrics = new HashSet<string>(StringComparer.Ordinal)
        { "execution_discipline" };
    var plan = new AnalysisPlan(
        "generic_query",
        new PeriodSpec("months", 12, null, null),
        AnalysisDataRoute.GeneratedSql,
        "leaders",
        [],
        ["public.sungero_wf_task"]);
    Check.True(!WorkflowContractValidator.ValidatePlan(
        plan,
        DateTimeOffset.Parse("2026-09-20T12:00:00+04:00"),
        dashboardMetrics).Ok);
}
```

Also test: empty metric, `months=0`, reversed range, more than 10 employee mentions, more than 12 relation hints, a relation hint outside `schema.table` syntax, blank SQL, SQL over 20,000 characters, and a SQL draft whose `MetricId` differs from the plan.

- [ ] **Step 2: Run the contract suite and verify it fails to compile**

```powershell
dotnet run --project tools/harness-tests -c Release -- workflowcontract
```

Expected: FAIL because the workflow records and validator do not exist.

- [ ] **Step 3: Add immutable contracts with explicit terminal state**

Define the public model-owned DTOs from the design spec. Annotate `AnalysisDataRoute` with `JsonStringEnumConverter<AnalysisDataRoute>` so Qwen uses the JSON strings `dashboardMetric` and `generatedSql` instead of integer enum values. Define the internal state as:

```csharp
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
```

Define `WorkflowLimits.MaxModelCalls = 5`, `MaxSqlRepairs = 1`, and `MaxReportRepairs = 1`. The counters are run-local and are never static.

Do not put connection strings, tokens, `HttpClient`, or raw provider responses in `WorkflowState`.

- [ ] **Step 4: Implement exact contract validation**

Use the exact signature `ValidatePlan(AnalysisPlan plan, DateTimeOffset asOf, IReadOnlySet<string> dashboardMetrics)`. It must call the existing period parser with `asOf`, enforce the collection limits, require the dashboard name to be in `dashboardMetrics`, and enforce the route-specific fields. `ValidateSqlDraft(SqlDraft draft, AnalysisPlan plan)` must require exact ordinal equality between `draft.MetricId` and `plan.MetricId`; SQL safety remains the responsibility of `SqlGuard` in Task 6.

- [ ] **Step 5: Run contract and full offline suites**

```powershell
dotnet run --project tools/harness-tests -c Release -- workflowcontract
dotnet run --project tools/harness-tests -c Release -- all
```

Expected: `FAIL=0` for both.

- [ ] **Step 6: Commit workflow contracts**

```powershell
git add Harness/Workflow/WorkflowContracts.cs Harness/Workflow/WorkflowContractValidator.cs Harness/Workflow/WorkflowLimits.cs tools/harness-tests/WorkflowContractTests.cs
git commit -m "feat: define analytics workflow contracts"
```

### Task 4: Build the strict Qwen JSON client on the existing Ario protocol

**Files:**
- Create: `Harness/Providers/IQwenJsonClient.cs`
- Create: `Harness/Providers/QwenJsonClient.cs`
- Create: `Harness/Providers/JsonObjectExtractor.cs`
- Test: `tools/harness-tests/QwenJsonClientTests.cs`

**Interfaces:**
- Produces: `Task<T> IQwenJsonClient.CompleteAsync<T>(string systemPrompt, object input, int maxTokens, CancellationToken ct)`.
- Consumes: configured token, model, endpoint, `HttpClient`, and `HarnessJson.Options`.

- [ ] **Step 1: Write transport tests with a fake HTTP handler**

Tests must assert that the request contains:

```json
{
  "model": "Qwen/Qwen3.8-27B",
  "temperature": 0.2,
  "chat_template_kwargs": { "enable_thinking": false }
}
```

and exactly one system plus one user message. Add cases for plain JSON, a single fenced JSON object, braces inside JSON strings, `content=null` with JSON in `reasoning_content`, HTTP 429 followed by success, HTTP 500, `finish_reason=length`, cancellation, two top-level JSON objects, and prose outside a JSON object.

- [ ] **Step 2: Run the provider suite and verify it fails**

```powershell
dotnet run --project tools/harness-tests -c Release -- qwenjsonclient
```

Expected: FAIL because the client and extractor do not exist.

- [ ] **Step 3: Define the generic interface and single-object extractor**

`JsonObjectExtractor.Extract(string text)` must scan JSON string escaping and nesting depth. It returns the only complete top-level object after removing an optional single Markdown fence. It throws `HarnessException` with `provider_protocol_error` when non-whitespace remains before or after the object or when a second object is present. Do not use a greedy regular expression.

- [ ] **Step 4: Implement the Qwen request and typed deserialization**

Serialize the user input with `HarnessJson.Options`. Send a fresh `HttpRequestMessage` per attempt, apply Bearer authorization, use `maxTokens` from the call, and retain the current one-time `Retry-After` behavior for 429. Deserialize with:

```csharp
var json = JsonObjectExtractor.Extract(content);
var value = JsonSerializer.Deserialize<T>(json, HarnessJson.Options);
return value ?? throw new HarnessException(new HarnessError(
    "provider_protocol_error",
    $"Qwen returned null for {typeof(T).Name}.",
    true));
```

Provider error messages must include status and a maximum 300-character sanitized detail, never request headers or tokens.

- [ ] **Step 5: Run provider, Qwen legacy, and cancellation tests**

```powershell
dotnet run --project tools/harness-tests -c Release -- qwenjsonclient
dotnet run --project tools/harness-tests -c Release -- qwen
dotnet run --project tools/harness-tests -c Release -- agent
```

Expected: all end with `FAIL=0`.

- [ ] **Step 6: Commit the structured Qwen transport**

```powershell
git add Harness/Providers/IQwenJsonClient.cs Harness/Providers/QwenJsonClient.cs Harness/Providers/JsonObjectExtractor.cs tools/harness-tests/QwenJsonClientTests.cs
git commit -m "feat: add strict qwen json client"
```

### Task 5: Implement stage-specific prompts and the model facade

**Files:**
- Create: `Harness/Workflow/IAnalysisStageModel.cs`
- Create: `Harness/Workflow/QwenAnalysisStageModel.cs`
- Create: `Harness/Workflow/WorkflowPrompts.cs`
- Test: `tools/harness-tests/StageModelTests.cs`

**Interfaces:**
- Produces:
  - `Task<AnalysisPlan> PlanAsync(PlanningInput input, CancellationToken ct)`
  - `Task<SqlDraft> DraftSqlAsync(SqlGenerationInput input, CancellationToken ct)`
  - `Task<SqlDraft> RepairSqlAsync(SqlRepairInput input, CancellationToken ct)`
  - `Task<ReportDraft> DraftReportAsync(ReportGenerationInput input, CancellationToken ct)`
  - `Task<ReportDraft> RepairReportAsync(ReportRepairInput input, CancellationToken ct)`
- Consumes: `IQwenJsonClient`, relevant catalog projection, verified selections, stored result manifests, and structured validation errors.

- [ ] **Step 1: Write tests that capture each stage prompt and input**

Use a recording `IQwenJsonClient`. Assert:

- planning receives the question, current time, metric summaries, and no database credentials;
- SQL generation receives only relations named by validated hints plus mandatory relationship and metric definitions;
- SQL repair receives the rejected SQL and structured error code/message;
- report generation receives actual `resultId`, column names/types, row count, truncation and bounded rows;
- report repair receives `ReportValidator` errors and the rejected report.

- [ ] **Step 2: Run the stage suite and verify it fails**

```powershell
dotnet run --project tools/harness-tests -c Release -- stagemodel
```

Expected: FAIL because the facade and prompt definitions do not exist.

- [ ] **Step 3: Add narrow input records**

Define inputs without infrastructure objects:

```csharp
public sealed record PlanningInput(
    string Question,
    DateTimeOffset AsOf,
    EntitySelection[] ConfirmedSelections,
    MetricSummary[] Metrics);

public sealed record SqlGenerationInput(
    string Question,
    AnalysisPlan Plan,
    Interpretation Interpretation,
    VerifiedEmployee[] Employees,
    CatalogProjection Catalog);

public sealed record ReportGenerationInput(
    string Question,
    Interpretation Interpretation,
    ResultManifest[] Results);
```

`ResultManifest` includes bounded row data but excludes connection details and provider messages.

- [ ] **Step 4: Write five explicit prompts**

Each prompt states the exact DTO fields, requires one JSON object, prohibits Markdown, and explains that server validation is authoritative. The SQL prompt must require named parameters `@from`, `@to`, and `@selected_employee_N` when the supplied interpretation/selections require them. The report prompt must prohibit literal numbers in title/commentary/templates and require `{{factId}}` for every numeric statement.

- [ ] **Step 5: Implement the facade and validate immediately after deserialization**

Use maximum token budgets: plan 800, SQL 1400, SQL repair 1400, report 1600, report repair 1600. Call `WorkflowContractValidator` after plan and SQL responses. Report validation occurs in Task 7 because it requires the run-scoped `ResultStore`.

- [ ] **Step 6: Run stage and provider suites**

```powershell
dotnet run --project tools/harness-tests -c Release -- stagemodel
dotnet run --project tools/harness-tests -c Release -- qwenjsonclient
```

Expected: both end with `FAIL=0`.

- [ ] **Step 7: Commit stage-specific model calls**

```powershell
git add Harness/Workflow/IAnalysisStageModel.cs Harness/Workflow/QwenAnalysisStageModel.cs Harness/Workflow/WorkflowPrompts.cs tools/harness-tests/StageModelTests.cs
git commit -m "feat: add qwen analytics stages"
```

### Task 6: Add typed server operations over the existing safety boundary

**Files:**
- Create: `Harness/Workflow/IAnalysisOperations.cs`
- Create: `Harness/Workflow/AnalysisOperations.cs`
- Modify: `Harness/Agent/ToolDispatcher.cs`
- Test: `tools/harness-tests/WorkflowOperationsTests.cs`
- Test: `tools/harness-tests/ToolsTests.cs`

**Interfaces:**
- Produces:
  - `Task<PreparationResult> PrepareAsync(AnalysisRequest request, RunContext context, CancellationToken ct)`
  - `Task<EntityResolutionResult> ResolveAsync(AnalysisPlan plan, RunContext context, CancellationToken ct)`
  - `Task<ResultPage> ExecuteDashboardAsync(AnalysisPlan plan, RunContext context, CancellationToken ct)`
  - `Task<ResultPage> ExecuteSqlAsync(AnalysisPlan plan, SqlDraft draft, RunContext context, CancellationToken ct)`
- Consumes: `ToolDispatcher`, `IEmployeeResolver`, `AnalyticsCatalog`, verified selections, and existing server parameter bindings.

- [ ] **Step 1: Write safety-focused operation tests**

Cover these inputs with a `CapturingQueryExecutor`:

- two confirmed employees produce `selected_employee_1` and `selected_employee_2` bound to their verified IDs;
- a personal metric query missing either parameter throws `missing_selection_binding` and `CallCount` stays zero;
- a month period query missing `@from` or `@to` throws `missing_period_binding` and `CallCount` stays zero;
- an ambiguous employee mention returns candidates and `CallCount` stays zero;
- a relation outside `AnalyticsCatalog.AllowedRelations` is rejected and `CallCount` stays zero;
- `INSERT`, `UPDATE`, `DELETE`, DDL, and `nextval` remain rejected by the existing guard.

- [ ] **Step 2: Run the operation suite and verify it fails**

```powershell
dotnet run --project tools/harness-tests -c Release -- workflowoperations
```

Expected: FAIL because the typed operations do not exist.

- [ ] **Step 3: Implement the typed facade without duplicating policy**

`AnalysisOperations` may construct server-owned `ModelAction` values and delegate to `ToolDispatcher` during the migration. Centralize that conversion in one private method:

```csharp
private static ModelAction ServerAction(string name, object arguments) =>
    new(
        name,
        JsonSerializer.SerializeToElement(arguments, HarnessJson.Options),
        JsonSerializer.SerializeToElement(new { role = "server", content = name }));
```

Do not copy SQL validation or parameter binding into the workflow namespace. Expose focused internal methods from `ToolDispatcher` only when a typed call cannot be represented without losing validation.

- [ ] **Step 4: Lock interpretation before data execution**

Apply `set_context` from the validated plan before executing either route. Ensure the context cannot change once a result exists. Dashboard routes must continue rejecting confirmed selections when the selected dashboard tool does not support them.

- [ ] **Step 5: Run operation, tool, SQL, and database-independent suites**

```powershell
dotnet run --project tools/harness-tests -c Release -- workflowoperations
dotnet run --project tools/harness-tests -c Release -- tools
dotnet run --project tools/harness-tests -c Release -- sql
dotnet run --project tools/harness-tests -c Release -- all
```

Expected: all available suites end with `FAIL=0`.

- [ ] **Step 6: Commit typed operations**

```powershell
git add Harness/Workflow/IAnalysisOperations.cs Harness/Workflow/AnalysisOperations.cs Harness/Agent/ToolDispatcher.cs tools/harness-tests/WorkflowOperationsTests.cs tools/harness-tests/ToolsTests.cs
git commit -m "feat: add typed analytics operations"
```

### Task 7: Make report drafting evidence-bound and independently repairable

**Files:**
- Create: `Harness/Workflow/ReportDraftService.cs`
- Create: `Harness/Workflow/ResultManifestFactory.cs`
- Modify: `Harness/Reports/ReportValidator.cs`
- Test: `tools/harness-tests/ReportWorkflowTests.cs`
- Test: `tools/harness-tests/ReportsTests.cs`

**Interfaces:**
- Produces: `Task<ReportDraftOutcome> ReportDraftService.CreateAsync(string question, RunContext context, int remainingModelCalls, CancellationToken ct)`, where `ReportDraftOutcome` contains the rendered report, model-call count, and repair count.
- Consumes: `IAnalysisStageModel`, `ResultStore`, `Interpretation`, `ReportValidator`, and `ReportRenderer`.

- [ ] **Step 1: Write report source-integrity tests**

Add tests for:

- a block referencing actual `r1` and existing columns completes;
- `tool:leaders`, an empty `resultId`, `r404`, and a wrong column are rejected;
- a numeric literal in title, commentary, or text outside `{{fact}}` is rejected;
- a fact over an existing numeric cell renders the exact stored value;
- a fact over a truncated cell is rejected;
- an invalid first report is repaired once;
- two invalid reports throw `invalid_report` and do not produce `RenderedReport`.

- [ ] **Step 2: Run the report workflow suite and verify it fails**

```powershell
dotnet run --project tools/harness-tests -c Release -- reportworkflow
```

Expected: FAIL because `ReportDraftService` does not exist.

- [ ] **Step 3: Build bounded result manifests from stored results**

`ResultManifestFactory` must derive identifiers and columns from `ResultStore.All()`, include at most 50 rows per result in the prompt, and carry the full stored row count and truncation flags. It must never accept a source name supplied by the model.

- [ ] **Step 4: Implement one repair with structured validator feedback**

Call `DraftReportAsync`, replace its interpretation with `context.Interpretation`, validate, and return through `ReportRenderer`. On the first validation failure call `RepairReportAsync` with all validator codes/messages and the same manifests. If `remainingModelCalls` cannot fund the repair or the second validation fails, throw one `HarnessException` whose code is `invalid_report` and whose message joins sanitized validation messages. Return `ModelCalls` as 1 or 2 and `Repairs` as 0 or 1 so the executor can update `WorkflowState` exactly once.

- [ ] **Step 5: Keep source failures specific**

When useful to users and tests, preserve `unknown_result`, `unknown_column`, `truncated_fact`, and `unverified_numeric_text` in `AgentStep.Error`. Convert them to the public terminal status in Task 8; do not collapse every condition into a provider error.

- [ ] **Step 6: Run report suites**

```powershell
dotnet run --project tools/harness-tests -c Release -- reportworkflow
dotnet run --project tools/harness-tests -c Release -- reports
```

Expected: both end with `FAIL=0`.

- [ ] **Step 7: Commit evidence-bound report generation**

```powershell
git add Harness/Workflow/ReportDraftService.cs Harness/Workflow/ResultManifestFactory.cs Harness/Reports/ReportValidator.cs tools/harness-tests/ReportWorkflowTests.cs tools/harness-tests/ReportTests.cs
git commit -m "feat: bind workflow reports to stored evidence"
```

### Task 8: Assemble the deterministic Agent Framework graph

**Files:**
- Create: `Harness/Workflow/PrepareExecutor.cs`
- Create: `Harness/Workflow/PlanExecutor.cs`
- Create: `Harness/Workflow/ResolveEntitiesExecutor.cs`
- Create: `Harness/Workflow/AcquireDataExecutor.cs`
- Create: `Harness/Workflow/DraftReportExecutor.cs`
- Create: `Harness/Workflow/FinishExecutor.cs`
- Create: `Harness/Workflow/AnalysisWorkflow.cs`
- Test: `tools/harness-tests/WorkflowTests.cs`

**Interfaces:**
- Produces: `AnalysisWorkflow : IAnalysisRunner`.
- Consumes: `IAnalysisStageModel`, `IAnalysisOperations`, `ReportDraftService`, `TimeProvider`, and Microsoft Agent Framework Workflows.

- [ ] **Step 1: Write end-to-end workflow tests with scripted dependencies**

Pin these paths:

1. valid dashboard plan → one dashboard call → valid report → `completed`;
2. valid SQL plan → one SQL draft → one executor call → valid report → `completed`;
3. invalid first SQL → one repair → one executor call → `completed`;
4. two invalid SQL drafts → zero executor calls → `failed`;
5. ambiguous employee → zero model SQL/report calls and `needs_clarification`;
6. empty result → no report call and `no_data`;
7. provider failure before data → `failed`;
8. provider/report failure after stored data → `incomplete`;
9. cancellation/budget expiry → `incomplete` with no later executor calls;
10. truncated result → warning retained in `AnalysisResponse`.

- [ ] **Step 2: Run the workflow suite and verify it fails**

```powershell
dotnet run --project tools/harness-tests -c Release -- workflow
```

Expected: FAIL because the executors and `AnalysisWorkflow` do not exist.

- [ ] **Step 3: Implement stateless typed executors**

Use `Executor<TInput,TOutput>` and return a new `WorkflowState` from each node. Every executor begins with:

```csharp
if (state.IsTerminal)
    return state;
```

except `PrepareExecutor`, whose input is `AnalysisRequest`, and `FinishExecutor`, whose output is `AnalysisResponse`. Record stable `AgentStep.Tool` names from the design spec. Do not record prompts or chain-of-thought.

Before every Qwen call, require `state.ModelCalls < WorkflowLimits.MaxModelCalls`; update the immutable counter after the call. `AcquireDataExecutor` permits at most one SQL repair and `DraftReportExecutor` applies the counts returned by `ReportDraftOutcome`. Add assertions that no path can exceed five model calls, one SQL repair, or one report repair.

- [ ] **Step 4: Build a fresh graph per run**

Use this topology:

```csharp
var builder = new WorkflowBuilder(prepare);
builder.AddEdge(prepare, plan);
builder.AddEdge(plan, resolve);
builder.AddEdge(resolve, acquire);
builder.AddEdge(acquire, report);
builder.AddEdge(report, finish).WithOutputFrom(finish);
var workflow = builder.Build();
```

Build fresh executor instances for every `RunAsync` call so run state cannot leak across requests.

- [ ] **Step 5: Map terminal errors and extract only the finish output**

Run with the linked 120-second token. Select the single `ExecutorCompletedEvent` whose `ExecutorId` is `finish` and require its `Data` to be `AnalysisResponse`. Map errors using whether `context.HasStoredResults`: provider or validation errors before data are `failed`; after data they are `incomplete`. Preserve `no_data` and `needs_clarification` without passing through later model stages.

- [ ] **Step 6: Run workflow, agent, result-isolation, and cancellation suites**

```powershell
dotnet run --project tools/harness-tests -c Release -- workflow
dotnet run --project tools/harness-tests -c Release -- agent
dotnet run --project tools/harness-tests -c Release -- results
```

Expected: all end with `FAIL=0`.

- [ ] **Step 7: Commit the workflow graph**

```powershell
git add Harness/Workflow/PrepareExecutor.cs Harness/Workflow/PlanExecutor.cs Harness/Workflow/ResolveEntitiesExecutor.cs Harness/Workflow/AcquireDataExecutor.cs Harness/Workflow/DraftReportExecutor.cs Harness/Workflow/FinishExecutor.cs Harness/Workflow/AnalysisWorkflow.cs tools/harness-tests/WorkflowTests.cs
git commit -m "feat: add deterministic analytics workflow"
```

### Task 9: Wire rollout, evaluation, and operational documentation

**Files:**
- Modify: `Program.cs` (configuration record only)
- Modify: `Program.Harness.cs:54-121`
- Modify: `Harness/Hosting/HarnessHost.cs`
- Modify: `config.example.json` if present; otherwise modify the documented configuration example in `README.md`
- Modify: `tests/analysis_smoke.py`
- Create: `tests/fixtures/analysis-workflow-cases.json`
- Modify: `docs/technical/analytics-harness-v2.md`
- Modify: `CLAUDE.md`
- Test: `tools/harness-tests/HostingTests.cs`

**Interfaces:**
- Produces: configuration `Analytics.Engine` with `workflow` and `legacy`, production `AnalysisWorkflow` wiring, and a repeatable offline/live evaluation report.
- Consumes: all interfaces delivered by Tasks 1-8.

- [ ] **Step 1: Add failing configuration and snapshot tests**

Assert that a run captures engine, endpoint, model, token and database connection once. Changing global configuration after capture must not affect the active run. Assert unknown engine values fail closed with `invalid_analytics_engine` rather than silently selecting legacy.

- [ ] **Step 2: Add explicit production wiring**

Extend analytics configuration with:

```csharp
public string Engine { get; set; } = "workflow";
```

In `CreateAnalysisRunner`, construct the existing catalog, resolver, read-only executor and dispatcher once. For `workflow`, create `QwenJsonClient`, `QwenAnalysisStageModel`, `AnalysisOperations`, `ReportDraftService`, and `AnalysisWorkflow`. For `legacy`, return the existing `AnalysisAgent`. Do not select an engine based on model-name heuristics.

- [ ] **Step 3: Add the concrete evaluation cases**

Create JSON cases for these questions:

```json
[
  {"id":"compare_two_people_12m","question":"Сравни количество поручений у Ивана Иванова и Босова Александра за 12 месяцев"},
  {"id":"one_person_6m","question":"Сколько поручений было у Ивана Иванова за последние 6 месяцев"},
  {"id":"completed_two_people","question":"Сравни выполненные задания Ивана Иванова и Босова Александра за год"},
  {"id":"employee_overdue_3m","question":"Покажи просроченные задания Ивана Иванова за 3 месяца"},
  {"id":"discipline_year","question":"Покажи исполнительскую дисциплину по месяцам за год"},
  {"id":"department_overdue","question":"Покажи просрочку по подразделениям"},
  {"id":"ambiguous_name","question":"Покажи поручения Иванова за год"},
  {"id":"unknown_employee","question":"Покажи поручения Несуществующего Сотрудника за год"},
  {"id":"empty_range","question":"Покажи поручения за период с 1 января 1990 по 1 февраля 1990"},
  {"id":"generic_top_performers","question":"Кто получил больше всего заданий за последние 30 дней"}
]
```

Attach machine assertions for status, required entity resolution, period binding, single source identity, absence of unknown result references, and no fabrication on empty data.

- [ ] **Step 4: Extend smoke output with workflow evidence**

For `--live`, write a sanitized JSON artifact under `artifacts/analytics-eval/` containing case ID, status, elapsed milliseconds, step names/statuses/error codes, result IDs, and effective SQL. Exclude rows, tokens, headers, connection strings and provider payloads. Return exit code 1 for semantic assertion failures and 2 only when the provider or RX is unavailable.

- [ ] **Step 5: Run the complete offline gate**

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet run --project tools/harness-tests -c Release -- all
python tests/analysis_smoke.py http://localhost:5080
python tests/smoke.py http://localhost:5080
node tests/charts.test.js
node tests/analysis.test.js
```

Expected: every command exits 0. Start the freshly built server before HTTP smoke and stop it afterward.

- [ ] **Step 6: Run the live comparative gate**

Run every evaluation case three times with `Analytics.Engine=legacy`, then three times with `Analytics.Engine=workflow`, using the same Qwen endpoint and database snapshot window. Acceptance requires:

- no safety assertion failure;
- no completed response with an absent source or unverified number;
- workflow semantic pass rate at least equal to legacy;
- at least 90% of successful workflow runs use zero or one SQL repair;
- the personal comparison case uses the requested rolling 12-month period and the same `resultId` for chart and facts.

Save only the sanitized evaluation artifacts.

- [ ] **Step 7: Update operating documentation**

Document the workflow stages, engine rollback switch, package lock, stable step names, Qwen target, live evaluation command, and the unchanged read-only boundary. Remove GigaChat-specific explanations from the target workflow section while keeping the legacy provider documented as rollback compatibility.

- [ ] **Step 8: Verify self-contained publish after full wiring**

```powershell
dotnet restore --locked-mode
dotnet publish -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\workflow-publish
```

Expected: exit 0; `catalog.json`, static files, Agent Framework assemblies and the executable are present.

- [ ] **Step 9: Commit rollout and documentation**

```powershell
git add Program.cs Program.Harness.cs Harness/Hosting/HarnessHost.cs tests/analysis_smoke.py tests/fixtures/analysis-workflow-cases.json docs/technical/analytics-harness-v2.md CLAUDE.md README.md
if (Test-Path -LiteralPath .\config.example.json) { git add config.example.json }
git commit -m "feat: switch analytics to qwen workflow"
```

If either optional file does not exist, omit it from `git add`; do not create a secret-bearing `config.json`.

### Task 10: Final branch verification and review handoff

**Files:**
- Review only: all files changed by Tasks 1-9
- Create (ignored working record): `.superpowers/sdd/2026-09-20-qwen-maf-analytics-workflow/final-verification.md`

**Interfaces:**
- Produces: a reviewable verification record and a branch ready for integration.
- Consumes: the complete implementation and all acceptance commands.

- [ ] **Step 1: Inspect the final diff for unintended scope**

Run:

```powershell
git status --short
git diff --stat
git diff --check
git diff -- . ':!server*.log' ':!artifacts/**'
```

Expected: no whitespace errors, secrets, generated logs, live data rows, unrelated UI rewrites, or database writes.

- [ ] **Step 2: Run the final clean verification**

From a clean process state, run locked restore, Release build, all offline .NET tests, both Python smoke suites, both Node suites, and self-contained publish using the commands from Task 9. Record exact command, exit code and `PASS/FAIL/BLOCKED` in `final-verification.md`.

- [ ] **Step 3: Review the five high-risk boundaries manually**

Confirm from code and tests that:

1. no workflow node can bypass `ToolDispatcher`/`SqlGuard`/`ReadOnlyExecutor`;
2. Qwen never supplies employee IDs directly to SQL parameters;
3. every report block/fact resolves against the current run's `ResultStore`;
4. cancellation prevents later model or database calls;
5. `legacy` rollback does not become the silent fallback for an invalid engine value.

- [ ] **Step 4: Request whole-branch code review**

Use `superpowers:requesting-code-review` with the design spec, this plan, the branch diff, and `final-verification.md`. Resolve every correctness or security finding, rerun the affected focused suite, then rerun the complete final gate if production code changed.

- [ ] **Step 5: Commit verification fixes and record final HEAD**

```powershell
git add -u
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -m "test: verify qwen analytics workflow" }
git rev-parse HEAD
```

Do not add `config.json`, server logs, publish output, or evaluation artifacts containing live rows.
