# Handoff: Qwen analytics workflow

## Mission

Finish the governed analytics workflow described by the design and implementation plan, make the live Qwen gate pass without weakening the read-only/evidence boundaries, then complete Task 10 final verification and branch review.

The analytics model is exclusively `Qwen/Qwen3.8-27B`. In this document, `legacy` means the old harness architecture using the same Qwen endpoint/model. It never means a GigaChat fallback.

## Read these files first

1. `docs/superpowers/specs/2026-09-20-qwen-maf-analytics-workflow-design.md`
2. `docs/superpowers/plans/2026-09-20-qwen-maf-analytics-workflow.md`
3. `.superpowers/sdd/2026-09-20-qwen-maf-analytics-workflow/progress.md`
4. `.superpowers/sdd/2026-09-20-qwen-maf-analytics-workflow/task-9-report.md`
5. `.superpowers/sdd/2026-09-20-qwen-maf-analytics-workflow/task-9-live-debug.md`
6. This handoff.

Use `superpowers:subagent-driven-development`, `superpowers:systematic-debugging`, and `superpowers:test-driven-development`. Use a fresh implementer for a new task or fix group and an independent reviewer after it. Do not mark a live gate as passed when it is unavailable, incomplete, or only structurally covered by unit tests.

## Repository state

- Worktree: `C:\Users\Korotaev_NO\Desktop\Проекты\ARMGov-Dashboard\.worktrees\harness-v2`
- Branch: `feat/evidence-harness-v2`
- Current HEAD: `a0eecc52890b81ab1c0e5897216211ab6a0808f0`
- Base before this program of work: `d799b57`
- Tracked tree is clean.
- `artifacts/` is intentionally untracked and contains sanitized live evaluations and publish output.
- No server should be listening on port 5080 at handoff time.
- The ignored local `config.json` contains real provider and database credentials. It was restored byte-for-byte after live runs. Never print, stage, copy into a report, or commit its tokens/passwords, including preset credentials.

Before doing anything, run:

```powershell
git status --short
git branch --show-current
git rev-parse HEAD
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object LocalPort -eq 5080
```

Expected tracked state: no modified tracked files. `?? artifacts/` is expected.

## Non-negotiable boundaries

1. Database access remains read-only. Workflow code must not bypass `ToolDispatcher`, `SqlGuard`, `SqlScopePolicy`, or `ReadOnlyExecutor`.
2. Qwen never supplies employee IDs used as SQL values. The server resolves candidates and binds verified `selected_employee_N` parameters.
3. SQL uses only catalog-approved relations/fields and server-owned period/selection bindings.
4. Reports use only current-run `ResultStore` IDs, columns, rows, and facts. No model-supplied tool alias is a source identity.
5. Invalid engine/model configuration fails closed. Never silently fall back to legacy or GigaChat.
6. Cancellation and terminal states prevent later model and database side effects.
7. Live artifacts are sanitized. They may contain case ID, status, elapsed time, step name/status/error code/result ID, and effective SQL. They must not contain rows, provider payloads, headers, credentials, connection strings, or raw exception messages.
8. Do not widen SQL policy merely to make a generated query pass. First prove that the SQL construct is safe and necessary.

## Implemented and reviewed work

| Area | Commits | State |
|---|---|---|
| Runner seam | `6089c90` | reviewed clean |
| Locked Agent Framework runtime | `539eaf8` | reviewed clean |
| Immutable workflow contracts | `20eb53e`, `f451366` | reviewed clean |
| Strict Qwen JSON transport | `dec3e81`, `14b3313` | reviewed clean |
| Qwen stage facade/prompts | `e236940`, `473bd09` | reviewed clean |
| Typed server operations | `028d295`, `ce03a2a` | reviewed clean |
| Evidence-bound report service | `3127793`, `d5855df`, `bec1c67` | reviewed clean |
| Deterministic MAF graph | `a03072f`, `614a7e0`, `7e64c51` | scoped re-review clean |
| Production wiring/evaluator/docs | `5338f31` | offline/publish green; live rollout failed |
| Planning contract alignment | `a0eecc5` | offline/live probe green at plan stage; independent review was interrupted and must be repeated |

Do not rewrite these commits. Add focused commits for verified fixes.

## Current architecture

`POST /api/ai/analysis` captures engine, endpoint, model, token, and DB connection once per run. `Analytics.Engine=workflow` builds:

```text
PrepareExecutor
  -> PlanExecutor
  -> ResolveEntitiesExecutor
  -> AcquireDataExecutor
  -> DraftReportExecutor
  -> FinishExecutor
```

Production components are `QwenJsonClient`, `QwenAnalysisStageModel`, `AnalysisOperations`, `ReportDraftService`, and `AnalysisWorkflow`. A fresh MAF graph is built for every request. `Analytics.Engine=legacy` retains the old agent only for rollback/comparison, on Qwen.

Planning now receives catalog-owned summaries for all catalog metrics, `generic_query`, and all qualified relation names with safe titles/descriptions. It does not receive fields, credentials, or infrastructure data. The planning prompt pins the exact `AnalysisPlan` JSON shape.

## Verification evidence at current HEAD

After `a0eecc5`:

```text
stagemodel: PASS=8 FAIL=0
workflow:   PASS=19 FAIL=0
all:        PASS=226 FAIL=0
```

The earlier complete Task 9 offline gate at `5338f31` was also green:

```text
dotnet restore --locked-mode                         PASS
dotnet build -c Release --no-restore                PASS, 0 warnings
dotnet run --project tools/harness-tests -- all     PASS=225
python tests/analysis_smoke.py http://localhost:5080 PASS=21
python tests/smoke.py http://localhost:5080          PASS=167
node tests/charts.test.js                            PASS=37
node tests/analysis.test.js                          PASS=23
self-contained win-x64 publish                      PASS
```

The final complete gate must be rerun after all remaining production changes.

## Live history

### Baseline at `5338f31`

Full 3 x 10 legacy and 3 x 10 workflow comparison:

- legacy on Qwen: 18/30 semantic pass (60%);
- workflow on Qwen: 0/30;
- workflow errors: 5 `provider_protocol_error`, 25 `workflow_failure`;
- no safety failure or unknown-result/data-after-clarification breach.

Artifact paths:

- `artifacts/analytics-eval/legacy-3x10.json`
- `artifacts/analytics-eval/workflow-3x10.json`

The 25 historical generic exception types cannot be recovered because the catch intentionally discarded details and no server log remains. A later ten-call shared-client probe did not reproduce them; all ten failed with `provider_protocol_error`. Do not claim they were rate limits.

The confirmed planning root cause was an underspecified prompt/input. Qwen returned valid JSON with the wrong typed shape: a scalar period, an unsupported route label, and semantic words instead of allowed `schema.table` relations. The planner also had no relation allowlist from which to choose.

### Planning fix at `a0eecc5`

One sanitized workflow run across all ten cases:

- plan passed 10/10;
- all 10 reached a post-plan stage;
- evaluator passed 7/10;
- statuses: 6 `needs_clarification`, 1 `no_data`, 2 `failed`, 1 `incomplete`;
- remaining errors: one `missing_period_binding`, one `unsupported_sql`, one post-plan `provider_protocol_error`;
- no availability or safety failures.

Artifact:

- `artifacts/analytics-eval/workflow-planning-contract-1x10.json`

The three failing cases are:

| Case | Last path | Error |
|---|---|---|
| `discipline_year` | dashboard result stored, then `draft_report` | `provider_protocol_error` |
| `department_overdue` | SQL acquisition before DB result | `unsupported_sql` |
| `empty_range` | SQL acquisition before DB result | `missing_period_binding` |

## Exact continuation order

### 1. Independently review the planning fix

Review only `5338f31..a0eecc5`. Confirm:

- deterministic server-owned metric/relation summaries;
- no relation fields, secrets, or connection data in planning input;
- exact period/route/array rules in the prompt;
- dashboard and generated-SQL route constraints;
- input allowlists are authoritative;
- strict deserialization and validation were not relaxed;
- tests cover the captured Qwen mismatch;
- all ten live cases truly progressed beyond `plan`.

Resolve Critical/Important findings before continuing. Record Minor findings in the SDD ledger.

### 2. Diagnose and fix the report protocol failure alone

Use `superpowers:systematic-debugging`. Reproduce only `discipline_year` with a temporary safe probe or recording wrapper. Capture the Qwen report response shape without credentials, result rows, provider headers, or hidden reasoning. Determine whether the failure is JSON extraction, deserialization shape, or response envelope.

Do not immediately assume the same fix as planning. Establish the exact malformed fields first. Likely contract surfaces to inspect are:

- `ReportDraft.report` wrapper;
- required `title`, `interpretation`, `blocks`, `facts`, `textTemplates`, `commentary`;
- `BlockSpec` kinds and required `resultId`/columns;
- `FactSpec` operations and `CellRef` shape;
- the rule that numeric text must use `{{factId}}` placeholders.

After root cause:

1. add a focused RED test using the captured response shape;
2. make the smallest prompt/transport/contract fix;
3. run `reportworkflow`, `stagemodel`, `workflow`, and `all`;
4. run only `discipline_year` live until it passes or reaches a specific validator repair path;
5. commit and request scoped review.

### 3. Diagnose SQL failures one at a time

#### `empty_range` / `missing_period_binding`

Capture the initial SQL draft, whether repair was attempted, and structured server error. The current graph repairs DTO-level `WorkflowContractValidator.ValidateSqlDraft` failures, but `missing_period_binding` is raised later by `ToolDispatcher` inside `ExecuteSqlAsync`; that path currently terminates without SQL repair.

Prove the call path with a failing test before changing it. A safe design should validate server binding/scope rules before the database call and feed those structured errors into the one allowed `RepairSqlAsync` call. It must never retry after a database side effect or after a result was stored. Prefer an explicit typed preflight operation over duplicating dispatcher policy in the workflow layer.

Required tests:

- first SQL missing `@from`/`@to` -> zero DB calls before repair;
- repaired SQL with both parameters -> exactly one DB call;
- second invalid SQL -> zero DB calls and terminal `failed`;
- selected employees still require every `@selected_employee_N` binding;
- cancellation between repair and execution prevents the DB call;
- total SQL repairs never exceeds one.

Then rerun only `empty_range` live.

#### `department_overdue` / `unsupported_sql`

Capture the rejected SQL and exact safe `SqlScopePolicy` reason. Do not infer it from the public code alone. Determine whether Qwen used a forbidden function/operator/CTE form or whether the safe parser rejected a construct that the catalog prompt should avoid.

- If Qwen chose an unnecessary unsupported construct, tighten the SQL prompt and repair feedback.
- Change `SqlScopePolicy` only if the construct is required, read-only, bounded, and covered by adversarial tests.
- Never add arbitrary functions, multi-statements, DDL/DML, sequences, or non-catalog relations to make the sample pass.

Run `workflowoperations`, `sql`, `workflow`, and `all`, then only this live case. Commit and review separately from the period-binding fix unless both failures have the same proven source.

### 4. Correct the live evaluator's clarification flow

The full acceptance gate requires a completed personal 12-month result whose chart and facts use the same result ID. The current evaluator always sends `selections: []`, so exact personal-name cases often end at a legitimate `needs_clarification` and the required source-identity assertion never runs.

Do not auto-select an arbitrary candidate. Extend fixture/evaluator behavior so personal completion runs use deterministic, verified selections:

1. issue the first request without IDs;
2. inspect only returned clarification candidates;
3. match the fixture's expected full employee names exactly and uniquely;
4. resubmit the original question with `EntitySelection { mention, employeeId }` values taken only from those candidates;
5. fail the semantic case if an expected name has zero or multiple exact matches;
6. keep `ambiguous_name` and `unknown_employee` as terminal clarification cases with no data execution.

The artifact remains sanitized and must not store employee IDs or names. Add offline evaluator tests for unique follow-up, ambiguity, no match, and no data-before-clarification. Then require the personal comparison period/source/fact assertions on the second response rather than skipping them for a clarification response.

### 5. Enforce Qwen-only analytics wiring

The user explicitly ruled that the analytics model is exclusively `Qwen/Qwen3.8-27B`.

Add configuration/wiring tests first. Both `workflow` and `legacy` analytics engines must use Qwen. If the analytics snapshot points to GigaChat or another unsupported model/provider, fail closed with a stable error such as `unsupported_analytics_model`; do not choose a provider from model-name heuristics and do not silently fall back.

The general dashboard may retain old GigaChat configuration UI/presets for unrelated compatibility if removing them would expand scope. The `/api/ai/analysis` harness path must be Qwen-only, and the docs must say so.

### 6. Add safe operational diagnostics

Do this after the functional failures above are resolved, unless a diagnostic is needed to prove a root cause.

- `WorkflowExecution.AddStep` currently hardcodes `ElapsedMs=0`. Measure monotonic per-step elapsed time for success and handled-error paths; add delayed fake tests.
- Normalize `HttpRequestException`/response-stream `IOException` at the Qwen provider boundary to a stable retryable transport error. Do not include exception messages, URLs, headers, tokens, or payloads.
- Keep `provider_protocol_error` distinct from transport and HTTP errors.
- Do not expose CLR exception names in the public API; coarse internal diagnostics may be written only to a sanitized local evaluation record.

### 7. Repeat gates incrementally

After each fix:

1. affected focused suite;
2. `dotnet run --project tools/harness-tests -c Release -- all`;
3. one affected live case;
4. a sanitized 1 x 10 workflow pass.

Run the complete 3 x 10 legacy + 3 x 10 workflow comparison only after the 1 x 10 workflow pass has no unexplained protocol/transport/workflow failures and includes at least one completed evidence-bound report.

Full live acceptance requires:

- no safety assertion failure;
- no completed response with an absent source or unverified number;
- workflow semantic pass rate at least the legacy rate from the same evaluation window;
- at least 90% of successful workflow runs use zero or one SQL repair;
- the completed personal comparison uses the requested rolling 12-month period;
- chart blocks and facts use the same stored `resultId`;
- empty data never produces a fabricated report.

### 8. Complete Task 10 only after live acceptance

Follow Task 10 in the main plan:

1. inspect the complete branch diff for scope, whitespace, generated files, secrets, live rows, and writes;
2. run locked restore, Release build, all .NET tests, both Python suites, both Node suites, and self-contained publish from a clean process state;
3. manually review the five high-risk boundaries listed in Task 10;
4. request whole-branch code review using the spec, plan, branch diff, and final verification record;
5. fix every Critical/Important correctness or security finding and rerun the complete gate after production changes;
6. use `superpowers:verification-before-completion` before claiming success;
7. use `superpowers:finishing-a-development-branch` only when the implementation and live gate are actually complete.

The separate WrenAI semantic-layer spike plan is not part of this continuation. Do not start it before the main Qwen workflow is accepted.

## Standard commands

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet run --project tools/harness-tests -c Release -- all

# Focused suites
dotnet run --project tools/harness-tests -c Release -- stagemodel
dotnet run --project tools/harness-tests -c Release -- workflow
dotnet run --project tools/harness-tests -c Release -- workflowoperations
dotnet run --project tools/harness-tests -c Release -- reportworkflow
dotnet run --project tools/harness-tests -c Release -- sql
dotnet run --project tools/harness-tests -c Release -- hosting

# HTTP/JS gates: start the freshly built server first and stop it afterward
python tests/analysis_smoke.py http://localhost:5080
python tests/smoke.py http://localhost:5080
node tests/charts.test.js
node tests/analysis.test.js

# Live workflow evaluation
python tests/analysis_smoke.py http://localhost:5080 --live --engine workflow --repetitions 1 `
  --artifact artifacts/analytics-eval/workflow-next-1x10.json

# Final publish
dotnet publish -c Release -r win-x64 --self-contained true --no-restore `
  -o .\artifacts\workflow-publish
```

When starting a background Windows process with `Start-Process`, use `-WindowStyle Hidden`. If local execution policy blocks it, use a managed terminal session and retain its session ID so it can be stopped reliably.

## Commit and artifact rules

- One focused commit per proven fix.
- Never stage `config.json`, `server*.log`, `artifacts/**`, `.superpowers/sdd/**`, or temporary probes.
- Before every commit: `git diff --check` and inspect `git status --short`.
- After a live run, restore `config.json` byte-for-byte and verify its hash/length without displaying secret values.
- Stop all server/evaluator processes and verify port 5080 is free.
- Attach exact test commands/results to the SDD task report and ledger.

## Definition of done

The branch is ready for integration only when all of the following are true:

- independent review of `a0eecc5` and every later fix is clean;
- full offline and publish gates pass from a clean process state;
- the complete Qwen-only live comparison passes every acceptance rule;
- no tracked or published artifact contains credentials or live result rows;
- the five high-risk boundaries have been manually reviewed;
- whole-branch review has no open Critical/Important issue;
- Task 10 `final-verification.md` records exact evidence;
- the branch completion skill has been followed.
