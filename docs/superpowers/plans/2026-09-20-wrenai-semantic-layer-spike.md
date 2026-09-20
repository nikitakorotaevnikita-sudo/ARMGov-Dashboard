# WrenAI Semantic Layer Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Determine with reproducible evidence whether WrenAI improves semantic SQL correctness enough to replace or augment `AnalyticsCatalog` for the ARMgov analytics workflow.

**Architecture:** The spike is isolated under `experiments/wrenai` and never becomes a production dependency. WrenAI models three representative Directum RX metrics; generated native PostgreSQL is evaluated by the existing safety policies and compared with current harness answers on fixed questions.

**Tech Stack:** Python 3.11+, WrenAI 0.14.0 with PostgreSQL connector, PostgreSQL test database, existing .NET 10 harness tests and Qwen evaluation cases.

**Spec:** `docs/superpowers/specs/2026-09-20-qwen-maf-analytics-workflow-design.md` (`WrenAI` section)

## Global Constraints

- This plan starts only after the main workflow can run the evaluation set; it does not block or modify the production runner.
- WrenAI is pinned to `0.14.0` and installed only in `experiments/wrenai/.venv`.
- Credentials live in the user Wren profile or environment variables; profile files, `.env`, generated query rows, and credentials are not committed.
- The first database target is the disposable `db/compose.yml` PostgreSQL instance. RX access remains read-only and uses the current allowlist and executor when comparative live evaluation is run.
- The spike models exactly three cases: personal instruction count, overdue assignments by department, and execution-discipline trend.
- Wren-produced native SQL must still pass `SqlGuard`, `SqlScopePolicy`, server parameter binding, and `ReadOnlyExecutor` before it is eligible for production use.

## Review Focus

- Wren model joins must not multiply tasks or assignments and inflate counts; pinned in Task 3.
- Personal instruction count must deduplicate `coalesce(nullif(maintask,0), id)` roots and exclude notification types; pinned in Task 3.
- Wren connection profiles and evaluation artifacts must not expose credentials or live rows; pinned in Tasks 1 and 5.
- Logical SQL using Wren model names must compile to native SQL containing only the current relation allowlist; pinned in Task 4.
- A higher answer rate does not pass the spike if the same question becomes nondeterministic or contradicts dashboard metrics; pinned in Task 5.

---

### Task 1: Create the isolated, reproducible Wren environment

**Files:**
- Create: `experiments/wrenai/README.md`
- Create: `experiments/wrenai/requirements.txt`
- Create: `experiments/wrenai/.gitignore`
- Create: `experiments/wrenai/connection.example.yml`
- Modify: `.gitignore`

**Interfaces:**
- Produces: a local `wren` CLI at version 0.14.0 with PostgreSQL support.
- Consumes: Python 3.11+ and user-provided environment variables for the disposable test database.

- [ ] **Step 1: Add the pinned dependency and ignore rules**

Use:

```text
wrenai[postgres]==0.14.0
```

Ignore `.venv/`, `target/`, `.wren/`, `.env`, `connection.yml`, `results/raw/`, and generated database rows. Keep `results/summary.json` and `results/decision.md` eligible for commit.

- [ ] **Step 2: Add a secret-free connection template**

`connection.example.yml` names `${ARMGOV_TEST_DB_HOST}`, `${ARMGOV_TEST_DB_PORT}`, `${ARMGOV_TEST_DB_NAME}`, `${ARMGOV_TEST_DB_USER}`, and `${ARMGOV_TEST_DB_PASSWORD}`. The README instructs the executor to run `wren docs connection-info postgres` and create the real ignored file from the schema printed by the pinned CLI.

- [ ] **Step 3: Install and verify in a project-local virtual environment**

```powershell
py -3.11 -m venv experiments\wrenai\.venv
& experiments\wrenai\.venv\Scripts\python.exe -m pip install --upgrade pip
& experiments\wrenai\.venv\Scripts\pip.exe install -r experiments\wrenai\requirements.txt
& experiments\wrenai\.venv\Scripts\wren.exe --version
```

Expected: the final command reports `0.14.0`.

- [ ] **Step 4: Initialize schema version 5 without a production profile**

```powershell
Push-Location experiments\wrenai
& .\.venv\Scripts\wren.exe context init
@'
schema_version: 5
name: armgov_spike
version: "1.0"
catalog: wren
schema: public
data_source: postgres
'@ | Set-Content -LiteralPath .\wren_project.yml -Encoding utf8
& .\.venv\Scripts\wren.exe context show
Pop-Location
```

Expected: `wren_project.yml`, `models/`, `views/`, and `knowledge/` exist; `wren_project.yml` declares `schema_version: 5` and `data_source: postgres`.

- [ ] **Step 5: Commit only reproducible, secret-free files**

```powershell
git add .gitignore experiments/wrenai/README.md experiments/wrenai/requirements.txt experiments/wrenai/.gitignore experiments/wrenai/connection.example.yml experiments/wrenai/wren_project.yml experiments/wrenai/models experiments/wrenai/views experiments/wrenai/knowledge
git diff --cached --check
git commit -m "spike: scaffold wren semantic project"
```

### Task 2: Capture the baseline questions and canonical harness results

**Files:**
- Create: `experiments/wrenai/eval/cases.json`
- Create: `experiments/wrenai/eval/run_baseline.py`
- Create: `experiments/wrenai/results/baseline.json`
- Test: `tests/analysis_smoke.py`

**Interfaces:**
- Produces: normalized baseline records `{caseId,status,columns,rowsHash,effectiveSql,assertions}`.
- Consumes: `/api/ai/analysis`, the existing workflow evaluation cases, and the disposable PostgreSQL fixture.

- [ ] **Step 1: Define ten questions across three metrics**

Include four personal-count variations, three overdue-by-department variations, and three execution-discipline periods. Each case declares expected metric ID, expected grain, required parameters, allowed relations, and whether exact row hashes are available on the disposable fixture.

- [ ] **Step 2: Write the baseline normalizer**

The script must sort object keys, preserve row order only when SQL declares ordering, hash normalized rows with SHA-256, and redact cell contents from console output. It exits 1 for a failed semantic assertion and 2 only for an unavailable server/provider.

- [ ] **Step 3: Start the current harness against the disposable database and collect baseline**

```powershell
python experiments/wrenai/eval/run_baseline.py --base-url http://localhost:5080 --cases experiments/wrenai/eval/cases.json --out experiments/wrenai/results/baseline.json
```

Expected: ten case records, no credentials, and no raw row values in the committed output.

- [ ] **Step 4: Commit the baseline harness and sanitized result**

```powershell
git add experiments/wrenai/eval/cases.json experiments/wrenai/eval/run_baseline.py experiments/wrenai/results/baseline.json
git commit -m "test: capture semantic layer baseline"
```

### Task 3: Model the three metrics in MDL and prove their grain

**Files:**
- Create: `experiments/wrenai/models/tasks.yml`
- Create: `experiments/wrenai/models/assignments.yml`
- Create: `experiments/wrenai/models/recipients.yml`
- Create: `experiments/wrenai/views/personal_instruction_counts.yml`
- Create: `experiments/wrenai/views/overdue_assignments_by_department.yml`
- Create: `experiments/wrenai/views/execution_discipline_monthly.yml`
- Create: `experiments/wrenai/knowledge/instructions.md`
- Create: `experiments/wrenai/eval/assert_grain.py`

**Interfaces:**
- Produces: compiled MDL exposing the three named analytical views.
- Consumes: exact fields, relationships, definitions, exclusions, and date semantics from `Harness/Catalog/catalog.json` and the canonical SQL tests.

- [ ] **Step 1: Define base models with only allowed columns**

Map `sungero_wf_task`, `sungero_wf_assignment`, and `sungero_core_recipient`. Declare task and assignment IDs as primary keys and the existing assignment→task, assignment→recipient, and recipient→department relationships with their real N:1 cardinality. Do not expose unrelated RX tables.

- [ ] **Step 2: Encode metric semantics explicitly**

`knowledge/instructions.md` must state:

- personal instruction count uses distinct root task ID, performer assignment, task creation period, instruction discriminator, and notification exclusion;
- overdue count uses assignment grain, `status='InProcess'`, non-null deadline, `deadline < now()`, and no root deduplication;
- execution discipline groups by month of task creation and separates completed on-time from overdue according to the current dashboard definition.

- [ ] **Step 3: Build and validate MDL**

```powershell
Push-Location experiments\wrenai
& .\.venv\Scripts\wren.exe context validate
& .\.venv\Scripts\wren.exe context build
Pop-Location
```

Expected: exit 0 and `target/mdl.json` is generated but remains ignored.

- [ ] **Step 4: Add fixture assertions against join multiplication**

`assert_grain.py` must create or reuse fixture cases with two assignments for one root task, one notification assignment, one overdue assignment, and two employees in the same department. It compares the three Wren view outputs with the canonical values already asserted by `DbTests.RootDeduplicationCountsDistinctInstructions` and related metric tests.

- [ ] **Step 5: Run grain assertions**

```powershell
& experiments\wrenai\.venv\Scripts\python.exe experiments\wrenai\eval\assert_grain.py
```

Expected: every metric reports `PASS`, including distinct-root and notification-exclusion cases.

- [ ] **Step 6: Commit semantic models**

```powershell
git add experiments/wrenai/models experiments/wrenai/views experiments/wrenai/knowledge/instructions.md experiments/wrenai/eval/assert_grain.py
git commit -m "spike: model armgov metrics in wren"
```

### Task 4: Compare Wren planning with the current catalog and safety policy

**Files:**
- Create: `experiments/wrenai/eval/generate_queries.py`
- Create: `experiments/wrenai/eval/check_native_sql.py`
- Create: `tools/wren-spike/wren-spike.csproj`
- Create: `tools/wren-spike/Program.cs`
- Create: `experiments/wrenai/results/query-comparison.json`

**Interfaces:**
- Produces: per-case logical SQL, native PostgreSQL SQL, safety outcome, metric/grain outcome, and normalized result hash.
- Consumes: Wren `dry-plan`, current Qwen endpoint, `SqlGuard`, `SqlScopePolicy`, `ReadOnlyExecutor`, and `AnalyticsCatalog`.

- [ ] **Step 1: Generate logical SQL using the same Qwen model**

For each case, send Qwen the Wren project context and require one Wren logical SQL statement over the three analytical views. Store SQL and structural metadata, but not returned database rows.

- [ ] **Step 2: Translate logical SQL to PostgreSQL without executing it**

```powershell
& experiments\wrenai\.venv\Scripts\wren.exe dry-plan --sql '<logical SQL>' -d postgres
```

`generate_queries.py` invokes this command with an argument array, never a shell-concatenated SQL string, and records the translated statement.

- [ ] **Step 3: Pass native SQL through production policies**

`tools/wren-spike` loads `catalog.json`, reads one SQL statement from standard input, and returns JSON containing `SqlGuard` and `SqlScopePolicy` results. It must not connect to RX. `check_native_sql.py` rejects any case with an unapproved relation, multiple statements, write keyword, forbidden function, missing period parameter, or missing employee binding.

- [ ] **Step 4: Execute eligible queries only through the existing read-only executor**

On the disposable database, call a small test-only adapter that supplies the same named parameters as the workflow and uses `ReadOnlyExecutor`. Hash normalized results and compare them with `baseline.json`.

- [ ] **Step 5: Run the comparison**

```powershell
& experiments\wrenai\.venv\Scripts\python.exe experiments\wrenai\eval\generate_queries.py
& experiments\wrenai\.venv\Scripts\python.exe experiments\wrenai\eval\check_native_sql.py
```

Expected: all executed statements pass production policy; failures remain explicit case failures and are not edited by the evaluator.

- [ ] **Step 6: Commit the comparison tooling and sanitized result**

```powershell
git add experiments/wrenai/eval/generate_queries.py experiments/wrenai/eval/check_native_sql.py tools/wren-spike experiments/wrenai/results/query-comparison.json
git commit -m "spike: compare wren sql with harness"
```

### Task 5: Run repeated evaluation and make a go/no-go decision

**Files:**
- Create: `experiments/wrenai/eval/summarize.py`
- Create: `experiments/wrenai/results/summary.json`
- Create: `experiments/wrenai/results/decision.md`

**Interfaces:**
- Produces: a signed-off decision of `adopt-semantic-core`, `adopt-context-only`, or `reject`.
- Consumes: three runs per case for current catalog and Wren, safety results, result hashes, elapsed time, and operational measurements.

- [ ] **Step 1: Run every case three times per approach**

Use identical Qwen endpoint/model, temperature, current-time anchor, database fixture and server limits. A case passes only when all three runs choose the correct metric/grain, pass SQL policy, and return the canonical result hash.

- [ ] **Step 2: Compute the decision metrics**

`summary.json` must contain:

- semantic pass rate;
- deterministic pass rate across three repeats;
- SQL safety rejection count;
- result-hash match count;
- median and p95 planning latency;
- prompt bytes;
- cold-start time and installed environment size;
- number of semantic source files and lines needed for the three metrics.

- [ ] **Step 3: Apply the fixed decision rule**

Choose `adopt-semantic-core` only when Wren reaches at least 90% semantic/deterministic passes, has zero safety violations, matches every canonical result for passed cases, and improves pass rate by at least 10 percentage points over the current catalog. Choose `adopt-context-only` when Wren context improves Qwen planning by at least 10 points but native SQL translation or runtime packaging fails the production constraints. Otherwise choose `reject`.

- [ ] **Step 4: Document concrete integration impact**

`decision.md` lists the winning decision, failed cases, changed operational dependencies, license/version, how `catalog.json` would map to MDL, and whether the .NET process would call a CLI child process, MCP service, or generated static context. It must explicitly retain `SqlGuard`, `ReadOnlyExecutor`, `ResultStore`, and `ReportValidator` in every adoption option.

- [ ] **Step 5: Verify the spike did not affect production**

```powershell
dotnet build -c Release
dotnet run --project tools/harness-tests -c Release -- all
python tests/analysis_smoke.py http://localhost:5080
git diff --check
```

Expected: production build and tests remain unchanged and pass.

- [ ] **Step 6: Commit the decision artifacts**

```powershell
git add experiments/wrenai/eval/summarize.py experiments/wrenai/results/summary.json experiments/wrenai/results/decision.md
git commit -m "docs: record wren semantic spike decision"
```
