# HANDOVER – Maintainer Notes for Qwen3.8-27B

**Read this first.** You are taking over maintenance of the 10/90 tenninety framework.
This document is written for a small-context model: it never requires you to hold the whole
repository in view. Every task below names the 1–3 files you must open and the exact symbol
to search for (`grep`). Do not read files that a recipe does not list.

Companion documents (read only the one your task needs):

| Document | When to read |
| --- | --- |
| `README.md` | One-screen overview + documentation index |
| `docs/DESIGN-RATIONALE.md` | Why things are the way they are (decision history, defect log) |
| `docs/JUDGMENT-CALLS.md` | **Before changing anything listed in §5** – each entry has a revisit trigger |
| `docs/SENIOR-GUIDE.md` | Command reference, config reference, audit vocabulary |
| `docs/JUNIOR-GUIDE.md` | User-level walkthrough; update it if user-facing behaviour changes |

---

## 1. Current state (verified before handover)

- Build: `dotnet build -c Release` → success, **0 warnings**.
- Tests: `dotnet test` → **1,063 passed / 0 failed / 10 skipped**, runtime ≈15s. The 10 skips
  are the Docker integration categories (generic + DockerCoder/DockerReviewer/DockerTester/
  DockerRestore/DockerEndToEnd) that are discovered but skipped until their explicit opt-in
  environment variables are provided; ordinary tests are fully Docker-independent.
- Live Docker gate (validated against Docker 29.7.2): with an exact local image
  (numeric non-root USER, no ENTRYPOINT) and the `tenninety-coder-model` network present, the
  deterministic DockerCoder, scripted DockerReviewer, offline DockerTester (including offline
  implicit-restore rejection), deterministic DockerEndToEnd and generic Docker
  transport/runtime/session/preflight categories all pass against the real daemon, including
  hardening inspection, quiescence/removal/absence proofs and workspace cleanup.
  `Category=DockerRestore` stays default-disabled: only its discovery and expected-negative
  prerequisite checks are exercised until a real operator contract exists.
- End-to-end smoke (offline mock mode): init → plan → start → all WPs DONE; conflict-WP
  deadlock path verified via CLI.
- Platform: .NET SDK 10.0.111, git ≥2.40, solution format `.slnx`. Targets pinned in
  `Directory.Build.props`: `net10.0`, `LangVersion=14.0`. **Do not change these.**
- Only NuGet dependency: Spectre.Console (TUI rendering). Keep it that way unless a task
  explicitly says otherwise.

## 2. Repository map (grep anchors)

```
src/Tenninety.Core          contracts & rules (no I/O beyond files)
  TenNinety.cs                constants: TenNinety.SchemaVersion, .WpStatus, .LayerRanks
  Models/Plan.cs              Plan, GlobalContext, ArchitectureMap, WorkPackage
  Models/RuntimeState.cs      RuntimeState, AttemptInfo   (.tenninety/state.json shape)
  Models/TenNinetyConfig.cs   TenNinetyConfig             (.tenninety/config.json shape;
                              budgets use C#14 `field` clamping)
  Stores/Stores.cs            Json serializer settings, PlanStore/StateStore/ConfigStore
  Stores/AuditLog.cs          append-only JSONL trail (compact lines, UTF-8 no BOM)
  Security/Sanitizer.cs       secret redaction regexes + filename globs (GlobMatch)
  Validation/PlanValidator.cs plan acceptance rules (Validate, TopologicalOrder, IdOrder)
  Validation/WpMarkers.cs     AMBIGUOUS/CONFLICT detection (case-SENSITIVE tokens)

src/Tenninety.Git/GitService.cs    IGitService + GitService (branches, squash promotion, revert)

src/Tenninety.Frontier      everything sent to / parsed from the Architect LLM
  Prompts/Prompts.cs          PlannerPrompt.System  ← THE v3.2 Enterprise blueprint prompt
                              RepairPrompt / PivotPrompt / RevertPrompt
  JsonExtractor.cs            tolerant first-JSON-object extraction from model output
  HttpFrontierClient.cs       OpenAI-compatible client (live frontier)
  MockFrontierClient.cs       deterministic offline planner/advisor

src/Tenninety.Execution     the engine room
  Abstractions.cs             WpContext, ICoderAgent, IReviewerAgent, ITesterAgent
  AgentFactory.cs             mock vs live agent selection (config.ProviderMode)
  ExecutionEngine.cs          retry loop; HandleThresholdAsync = escalation/BLOCKED logic
  Orchestrator.cs             SelectNextReady, ReportDeadlock, Pause/Resume
  PivotAndRevert.cs           PivotService.Apply, RevertService.RevertAsync
  Workspace.cs                loads stores+config+frontier for both CLI and TUI hosts
  Mock/MockAgents.cs          MockCoderAgent, MockReviewerAgent
  Testing/                    ShellTesterAgent, SandboxTesterGate, RestoreIntegrityValidator,
                              TestProjectDiscovery, TestOutputClassifier, TesterRunContext,
                              UnsafeHostTesterAgent
  Coding/                     SandboxCoderGate, CoderToolPlan
  Reviewing/                  SandboxReviewerGate, ReviewerProtocol
  Candidates/                 CandidateScanner, CandidatePromotionService, PromotionPolicy,
                              CandidateWorkspaceFactory, GitTreeMaterializer
  Sandbox/                    DockerCli (typed adapter), DockerCliProcessTransport,
                              DockerCliSandboxRuntime/Session, DockerSandboxPreflight,
                              SandboxResourceJournal, SandboxRecoveryService,
                              TrustedFileReader/TrustedPathValidation/TrustedWorkspaceDeletion
  CliCoderAgentBase.cs        shared process safety for aider/OpenCode/Pi
  Aider/, OpenCode/, Pi/      terminal coder adapters
  OpenAi/OpenAiAgents.cs      LocalChatClient, OpenAiReviewerAgent

src/Tenninety.Tui/TuiHost.cs        dashboard Draw() + keys [P][S][R][L][Q]
src/Tenninety.Cli                   Program.cs command switch; Commands/*.cs implementations
tests/Tenninety.Tests               ~1,073 tests across 50+ files (names match the area they pin)
```

## 3. Ground rules (violating these caused real bugs before)

1. **JSON contracts are camelCase via `Json.Options`** – field names must literally match
   the blueprint schema (`module`, `notes`, `architecture_map`, …). StoreTests asserts this.
2. **Audit log lines**: always `_audit.Append("UPPER_SNAKE", wpId?, detail)`; compact JSON,
   no BOM. If you add an event, add it to the vocabulary table in SENIOR-GUIDE §3.
3. **Spectre markup escapes**: interpolate user text through `Markup.Escape(...)`; literal
   brackets in markup are `[[P]]`. Unescaped `[WORD]` throws at render time.
4. **HTTP bodies**: build with `new StringContent(Json.Serialize(x), Encoding.UTF8,
   "application/json")`. `JsonContent.Create` resolves to broken overloads here.
5. **Clean tree invariant**: the orchestrator refuses dirty workspaces. Runtime state,
   temp/lock files, audit logs, and `control/` markers are ignored via
   `.tenninety/.gitignore` – keep all `RuntimeGitignoreMigration.RequiredLines` ignored.
6. **Marker detection is case-sensitive** (`CONFLICT`, uppercase only). Lowercase prose must
   never trigger the protocol; tests pin lookalikes ("UNAMBIGUOUS", "conflict resolved").
7. **Never rewrite git history.** Promotions are single SQUASH commits; undos go through `git revert`.
8. **Plan validation runs twice by design** (at `plan`, after every pivot). Any new rule you
   add to `PlanValidator.Validate` automatically guards pivots too – make sure pivot tests
   still pass when you tighten rules.
9. After any engine change, re-run the offline smoke (§6.2), not just unit tests.

## 4. Change recipes (touch as few files as possible)

Each recipe lists ALL files that must change together. Run §6 verification after.

| # | Task | Files | Anchor symbols |
|---|------|-------|----------------|
| R1 | Edit the blueprint prompt wording | `Frontier/Prompts/Prompts.cs` | `PlannerPrompt.System` (keep the JSON block in sync with the schema-sync rule below). Note: affects live mode only; mock ignores prompts |
| R2 | Add/change a plan validation rule | `Core/Validation/PlanValidator.cs` + `tests/.../PlanValidatorTests.cs` | `Validate` |
| R3 | Change retry/escalation behaviour | `Execution/ExecutionEngine.cs` (+ `JUDGMENT-CALLS.md` entry C3/C2 first!) | `HandleThresholdAsync` |
| R4 | Change scheduling order/rules | `Execution/Orchestrator.cs` | `SelectNextReady`, `ReportDeadlock` |
| R5 | Change mock agents' behaviour | `Execution/Mock/MockAgents.cs` | class names in §2 |
| R6 | Change live coder/reviewer protocol or prompts | coder: `CliCoderAgentBase.cs` + selected adapter; reviewer: `OpenAi/OpenAiAgents.cs` | `BuildArguments`, `OpenAiReviewerAgent` |
| R7 | Add an audit event | call `_audit.Append(...)` where it happens + SENIOR-GUIDE table | – |
| R8 | Add a CLI command | `Cli/Program.cs` switch + new static class in `Cli/Commands/` + help text in `Program.cs` (`Usage` const) | `"plan" =>` pattern |
| R9 | Change config fields/clamps | `Core/Models/TenNinetyConfig.cs` (+ defaults documented in SENIOR-GUIDE §5) | `MaxAttemptsBeforeEscalation` |
| R10 | Change TUI layout | `Tui/TuiHost.cs` (isolated `Draw()`; mind rule 3) | `Draw` |

### Schema-sync rule (the one trap that bites hardest)
The plan schema lives in FOUR places that must be edited together:
1. `Core/Models/Plan.cs` (types)
2. `Prompts/Prompts.cs` → `PlannerPrompt.System` JSON block
3. `MockFrontierClient.cs` (sample emission)
4. `tests/.../StoreTests.cs` (`Plan_json_uses_blueprint_field_names`)
Miss one and either the planner output stops parsing, the mock lies about the contract, or
a test fails. Grep for an existing field name (e.g. `architecture_map`) to find all four.

## 5. Open work – what stands between the framework and "final"

Priority order. For every item: read its JUDGMENT-CALLS entry BEFORE coding; move the entry
to DESIGN-RATIONALE §5 when done; keep `dotnet test` green.

### P0 – required before calling it production-ready
1. **Containerised sandboxing (external review Major 1) — SHIPPED.** Docker mode uses
   digest-pinned hardened role containers, exact disposable candidate workspaces, an internal
   Coder model network, offline Reviewer/Tester guests, optional accepted restricted Restore,
   lease-bound promotion, bounded cleanup, and startup recovery from a trusted resource journal.
   `unsafe-host` is explicit and Docker failures never fall back to it. Remaining work is
   operational live-Docker validation and continued hostile review, not host-role migration.
Historical items kept for context:

**Historical P0-1: Separate accident budget (Option B design) — ⛔ DECLINED BY OWNER, DO NOT BUILD.**
   During the issues walkthrough the owner chose to **keep current behaviour**: accidents
   tick the 10/20 quality budgets, and reviewer/tester/frontier crashes abort the daemon
   run (state is preserved; resume recovers). Rationale: serial mode means an infra outage
   has nothing useful to fall back on anyway, so a clean pause-with-message was judged
   equivalent in outcome and simpler to explain. Full design retained below only as
   historical reference (`ISSUE-2-DEEP-DIVE.md` is the plain-language record). Reopen only
   if the owner asks.
   Original specification (superseded): every failed cycle ticks the 10/20 quality counters,
   and reviewer/tester/frontier exceptions are *uncaught* and kill the whole daemon run.
   Implement the two-pile model:

   **Classification.**
   - *Accident* = any exception thrown by the coder agent, reviewer agent, tester agent,
     or by the frontier advice call inside `HandleThresholdAsync` (model offline, malformed
     response, IO error).
   - *Quality failure* (unchanged semantics) = review FAIL verdict · test FAIL exit ·
     coder produced no changes. These keep ticking 10/20 exactly as now.

   **Config** (`TenNinetyConfig`, camelCase, clamped like its siblings):
   - `"max_accident_retries": 5` (clamp ≥ 0).

   **Engine changes** (`ExecutionEngine.cs`):
   - Wrap the three uncaught call sites: reviewer call, tester call,
     and the advice fetch inside `HandleThresholdAsync`.
   - Accident path: do **not** increment `Count`/`Total`; increment a new
     `AttemptInfo.Accidents` counter; append to a new `AttemptInfo.AccidentNotes` list –
     never to `Feedback` (the coder must not read "your brain was offline" as work criticism).
   - Backoff between accident retries: 30 s doubling, capped at 5 min. Extract the delay
     behind an injectable delegate so tests pass a no-op.
   - Accident budget exhausted ⇒ job paused with status `PENDING`, audit
     `ACCIDENT_BUDGET_EXHAUSTED`, loud `ACTION REQUIRED: infrastructure – <cause>` log.
     Never mark the WP BLOCKED for accidents.
   - New outcome plumbing: `WpOutcome.InfraBlocked` → `OrchestratorExit.InfrastructureBlocked`
     → CLI exit code **5** ("infrastructure action required"); TUI banner treats it like pause.

   **Model/state**: add `Accidents` + `AccidentNotes` to `AttemptInfo`
   (`Core/Models/RuntimeState.cs`, serialized as `"accidents"` / `"accident_notes"`).

   **Audit vocabulary** (add to SENIOR-GUIDE table): `ACCIDENT_RECORDED` (detail names the
   tripping role), `ACCIDENT_BUDGET_EXHAUSTED`.

   **Tests required** (`ExecutionEngineTests`; inject no-op delay):
   - coder throws 7× (budget 5) → outcome InfraBlocked; `Total == 0`;
     `Feedback` contains no accident notes; audit shows ACCIDENT_* events.
   - coder throws 3× then succeeds → Done; `Total == 1`; `Accidents == 3`.
   - reviewer agent throws → engine survives (no crash), accident path used.
   - mixed: 1 review FAIL + several coder exceptions → quality `Total == 1`.

   **Files touched**: `Core/Models/RuntimeState.cs`, `TenNinetyConfig`,
   `ExecutionEngine.cs`, `Orchestrator.cs`, `Commands/StartAndRevert.cs` (exit 5),
   `TuiHost.cs` (banner), SENIOR-GUIDE (engine semantics §4, exit codes, audit table),
   JUNIOR-GUIDE troubleshooting row. Acceptance: all existing tests stay green +
   the four new cases above.

2. **Live-path integration coverage.** Stubbed HTTP-handler tests now cover Frontier wire
   contracts, bounded/error responses, and reviewer PASS/FAIL parsing. Remaining minimum bar:
   - fake `aider`, `opencode`, and `pi` executables that capture argv/environment and make a
     controlled workspace edit, proving all three process adapters end-to-end;
   - one opt-in test against actual local model endpoints before production use.
   New file: `tests/.../LiveAgentProcessTests.cs`; do not require network in the default suite.

### P1 – should land soon after first live use
3. **Lowercase marker normalisation (B2 risk) – ✅ DECIDED: keep uppercase-only for now.**
   Owner chose evidence-first (Option A): no normalisation until a live plan actually shows
   a lowercase marker slipping through. If that happens, the prepared fix is: at
   plan-parse time uppercase a leading case-insensitive `ambiguous:`/`conflict:` token so
   strict detection sees it. Files: `HttpFrontierClient.ParseAndValidatePlan` or a helper
   in `WpMarkers`; test in `PlanValidatorTests`.
4. **Layer-inversion escape hatch – ✅ DECIDED: keep strict always; flag stays contingency-only.**
   Owner confirmed hard rejection today; build `plan --allow-layer-inversion`
   (warning-only) only if live plans bounce repeatedly on inversions alone.
   Files: `Commands/PlanCommand.cs`, `PlanValidator.Validate(plan, bool strict)`.
5. **Thematic WP ids break ordering – ✅ DECIDED: keep numeric-first ordering as-is.**
   `IdOrder` extracts digits; word-named ids (`WP-LOGIN`) tie and fall back to
   alphabetical. Revisit only if real plans use non-numeric ids AND the order visibly
   bothers the owner; then add an optional `priority` schema field (using the four-file schema sync)
   or extend `IdOrder`.
6. **Spec-drift policy – ✅ DECIDED: silent fingerprint display stays.**
   Hash is recorded at acceptance and shown in status; mid-run spec edits raise no alarm.
   Sanctioned path remains OpenSpec change + pivot. Revisit only after a real drift
   incident confuses an actual run.

### P2 – known debt, schedule when convenient
7. Parallel workers: `max_concurrent_workers` knob exists; serial is enforced in the
   `Orchestrator` ctor. The seam is `SelectNextReady()` (pure). Merge policy needs rethink
   before enabling.
8. Non-mechanical reverts: currently refused by design (`RevertService`). Sketch for an
   automated flow is in JUDGMENT-CALLS D4.
9. Extract `Workspace.cs` out of `Execution` into a host project if a third host appears.
10. TUI polish: swap clear-redraw for Spectre `Live` if flicker complaints arrive; `Draw()`
    is isolated on purpose.
11. Hybrid coder protocol (diffs for edits) if context pressure shows up – design note in
    JUDGMENT-CALLS B1.

## 6. Verification playbook

Shell-specific lines are given for **bash** and **fish**; everything else is identical.

```bash
# 6.1 fast gate (run after EVERY change) – identical in bash and fish (fish ≥ 3)
dotnet build -c Release && dotnet test
```

**bash:**
```bash
# 6.2 full offline demo (after engine/orchestrator/CLI changes)
rm -rf /tmp/demo && mkdir /tmp/demo && cd /tmp/demo
printf '# Demo\n\n## Business Rules\n- Tasks have owners.\n' > spec.md
T=<repo>/src/Tenninety.Cli/bin/Release/net10.0/tenninety
$T init && $T plan --spec ./spec.md --yes && $T start --headless
$T status                      # expect all DONE, Tree clean
```

**fish:**
```fish
# 6.2 full offline demo – fish version ("set T", no "="; rest is the same)
rm -rf /tmp/demo; mkdir /tmp/demo; cd /tmp/demo
printf '# Demo\n\n## Business Rules\n- Tasks have owners.\n' > spec.md
set T <repo>/src/Tenninety.Cli/bin/Release/net10.0/tenninety
$T init && $T plan --spec ./spec.md --yes && $T start --headless
$T status                      # expect all DONE, Tree clean
```

```bash
# 6.3 failure paths (mock knobs in .tenninety/config.json; commit config edits first!)
#   reviewer_fail_attempts: 11      → escalation at 10, pass at 11
#   reviewer_ignores_advice: true   → BLOCKED at 20, deadlock exit 4
#   tester_fail_attempts: N         → mechanical-test feedback loop

# 6.4 conflict protocol (in a fresh planned workspace, edit plan.json to give a PENDING WP
#     "notes":"CONFLICT: ..." and no directives; commit plan.json before start; start must
#     skip it and exit 4 naming it)
```

Exit codes: `0` ok/paused/stopped · `1` error · `2` usage · `4` deadlock.

## 7. Live-mode bring-up checklist (never yet exercised – treat as unproven)

1. `docker compose up -d` (vLLM coder :8000, reviewer :8001; adjust image/model env).
2. `.tenninety/config.json`: `"provider_mode": "aider"`, dedicated coder endpoint `:8000/v1`
   and reviewer endpoint `:8001/v1`, with model names matching
   `--served-model-name`; real `frontier_endpoint`/`frontier_model`.
3. API key via environment variable (host only; never in files):
   ```bash
   # bash
   export TENNINETY_FRONTIER_API_KEY=...
   ```
   ```fish
   # fish
   set -x TENNINETY_FRONTIER_API_KEY ...
   ```
4. Small spec → `$T plan --spec ./spec.md` (interactive confirm) → inspect plan carefully.
5. `$T start --headless` on a throwaway repo; verify in order:
   coder CLI edits the tree and the engine commits · reviewer verdicts parse · sanitiser redacted secrets in
   any logged prompt tails · promotion merges · pause/resume across processes works.
6. Record deviations in JUDGMENT-CALLS (that's what revisit triggers are for).

## 8. Final notes

- The four docs under `docs/` plus README are part of the deliverable. A code change that
  makes any statement there false is incomplete until the doc is updated (one paragraph is
  usually enough; point edits, not rewrites).
- When unsure whether something is a bug or a decision: check JUDGMENT-CALLS first, then
  DESIGN-RATIONALE §5. Most surprising behaviours (clean-tree refusals, CONFLICT skipping,
  counter resets, squash merges) are deliberate and tested.
- Keep changes small and independently verified. The test suite runs in ~0.2s – there is
  no excuse for shipping red.
