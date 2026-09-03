# Design Rationale & Decision Log

**Project:** 10/90 tenninety – Spec-Driven Autonomous Framework, .NET v3.2
**Platform:** .NET 10 (`net10.0`) · **Language:** C# 14

This document explains *why* the framework is coded the way it is: which choices were
forced by the specification, which were left open and decided independently, and which
real defects surfaced during verification together with the resolutions chosen.
For the decisions where confidence was low or alternatives remained live – each with an
explicit revisit trigger – see [JUDGMENT-CALLS.md](JUDGMENT-CALLS.md).

---

## 0. Adopting the v3.2 Enterprise blueprint prompt

The operator supplied the authoritative **Frontier Model Blueprint Prompt (v3.2
Enterprise)** after initial implementation. It supersedes the sketch in Part VIII of the
original spec, and the framework was adjusted to fit it:

| Blueprint element | Framework change |
| --- | --- |
| `architecture_map` (bounded_contexts / core_entities / key_dependencies) | New optional `Plan.ArchitectureMap`; rendered by `tenninety plan` summary |
| `global_context.directory_structure` (`/src`, `/tests`) | New optional `GlobalContext.DirectoryStructure`; rendered at plan time; WP-001 acceptance criteria may reference it |
| `module` on every WP | New field; empty module is a validation warning (bounded-context hygiene) |
| `notes` on every WP + AMBIGUOUS/CONFLICT protocol | Marker detection (`WpMarkers`), validator allowances, scheduler exclusions – see below |
| Layers now include `UI` and `TEST-E2E` | Added to the layer-rank table (both hard-coded ranks) |
| "A WP in a lower layer cannot depend on a higher layer" | Upgraded from warning to **hard acceptance error** (when both layers have known ranks) |
| Planner system prompt | Rewritten to carry the enterprise blueprint verbatim-in-spirit: role, rules 1–6, ambiguity protocol, self-correction checklist, full output schema |

**Ambiguity protocol decisions (spec-gap calls of my own):**

1. **Markers are exact uppercase tokens in `notes`.** The blueprint writes them uppercase;
   detection is case-*sensitive* word-boundary matching so ordinary prose ("we resolved the
   conflict…") can never re-trigger the protocol. Verified by tests including lookalikes
   ("UNAMBIGUOUS", "CONFLICTING").
2. **CONFLICT ⇒ never scheduled.** Such WPs have no directives by protocol, so executing
   them would be pure hallucination fuel. `SelectNextReady` excludes them; when the rest of
   the queue drains, the orchestrator exits with deadlock and an audit entry explicitly
   naming the CONFLICT WPs awaiting human resolution.
3. **AMBIGUOUS remains executable.** Per blueprint these still carry directives built on
   recorded assumptions; they are surfaced loudly (plan summary warnings, ⚠AMBIGUOUS flags
   in status/TUI tables) instead of being blocked.
4. **A pivot REWORK resolves markers.** REWORK is the human's resolution mechanism, so
   applying one retires the markers (uppercase tokens stripped from notes) and stamps
   `[resolved by pivot REWORK: …]`. Without this, a conflicted WP could never re-enter the
   queue without hand-editing plan.json.
5. **Acceptance UX.** With flagged WPs present, `tenninety plan` defaults the confirm
   prompt to "No" and prints explicit counts; `--yes` still proceeds for scripted use but
   the warnings always print.

---

## 1. Platform and language: .NET 10, C# 14

### The requirement
The framework is titled *"10/90 .NET v3.2"*. That is read as: the framework itself is a
.NET application built on the **latest** .NET release – .NET 10 – compiled with **C# 14**
(`<LangVersion>14.0</LangVersion>`, pinned explicitly in `Directory.Build.props` so the
intent cannot silently drift).

### A correction worth recording honestly
The solution's MSBuild targets were `net10.0` from the start (every `.csproj` inherits
`<TargetFramework>net10.0</TargetFramework>` and builds only against the installed .NET 10
runtime), but several *presentation* artefacts leaked ".NET 8" wording: the sample spec's
tech stack (`.NET 8 Web API, EF Core 8`), a test fixture string, the local-coder system
prompt ("Senior .NET Engineer"), and the README framing. All were corrected to .NET 10 /
C# 14. Lesson applied: platform identity must live in one pinned place (`Directory.Build.props`)
and everywhere else reference it, not paraphrase it.

### Where C# 14 is actually used
New language features are adopted only where they make intent clearer, not decoratively:

| Feature | Location | Why |
| --- | --- | --- |
| Extension members (`extension` blocks) | `CoderResultExtensions` in `ExecutionEngine.cs` | `ProducesRealChange` reads as an instance *property* of a `CoderResult`, which is what it conceptually is; the old `this`-parameter method form obscured that |
| Field-backed properties (`field` keyword) | `TenNinetyConfig.MaxAttemptsBeforeEscalation`, `MaxTotalAttempts` | Budgets are clamped (`Math.Max(1, value)`) *on write*, including during `System.Text.Json` deserialization – no manual backing fields, no post-deserialize repair pass |

A deliberate consequence: a hand-edited `config.json` containing `"max_total_attempts": -5`
is clamped to `1` at load time, verified end-to-end (the orchestrator then blocks a failing
WP after exactly one attempt instead of looping forever).

---

## 2. Architecture: six projects mirroring the Triad

```
Core ◄── Git ◄──┐
     ◄── Frontier ── Execution ◄── Tui ◄── Cli
```

- **Forced by spec:** the Orchestrator/Executors/Frontier separation (Part I). The project
  boundaries encode it: `Execution` can talk to `Git` and `Frontier`, but `Frontier` knows
  nothing about git, and nothing below `Cli`/`Tui` prints to a console.
- **Decision (mine):** six small projects rather than one file-everything assembly or a
  generic "modules" bucket. The dependency arrows are enforced by project references, which
  is the cheapest architecture-test available: if someone makes the Frontier client import
  git logic, the build breaks.
- **Decision (mine):** `Workspace` lives in `Execution`, not `Cli`. It started in `Cli`
  and the TUI immediately needed it – moving it resolved would-be circular references.
  It bundles stores + config + frontier factory precisely because both hosts (headless
  CLI and interactive TUI) need the identical wiring.

### Dependencies: one package, everything else BCL
- Only external NuGet dependency: **Spectre.Console** (TUI rendering).
- **Decision (mine):** hand-rolled argument parsing instead of `System.CommandLine`.
  The command surface is eight verbs with flags; a parser dependency buys nothing and adds
  version churn risk. JSON is `System.Text.Json` (in-box), HTTP is `HttpClient` (in-box).
- **Decision (mine):** camelCase serialization settings globally, so files on disk match
  the specification's JSON examples byte-for-byte (`"schema_version"`, `"work_packages"`,
  `"frontier_advice_used"` …). A store round-trip test asserts those literal keys.

---

## 3. Behaviours mandated by the spec (implementation notes)

- **Serial execution with a parallel escape hatch.** `max_concurrent_workers` exists in
  config but the Orchestrator throws on any mode other than `serial`. The queue selector
  (`SelectNextReady`) is already a pure function returning "the next ready WP", which is
  exactly the seam a future worker-pool scheduler plugs into.
- **Strict-DAG acceptance.** Plans are validated *twice*: after Frontier generation
  (`tenninety plan`) and again after every pivot mutation (`PivotService.Apply`). A pivot
  that would break topological order is rejected before it can touch disk. Since the
  blueprint upgrade, validation also enforces layer ordering as a hard error (a lower
  layer never depends on a higher one) and surfaces AMBIGUOUS/CONFLICT markers; empty
  directives are legal only for CONFLICT WPs.
- **Git-first state.** Branches `work/<id>` per WP, promotion via an ALWAYS-squash merge
  (one identifiable commit per package – superseding the earlier ff-with-fallback rule
  after external review M8), branch deletion only with `-d`/`-D` (refuses unmerged unless
  content is provably merged), history changes only via `git revert`. There is no force
  push anywhere in the codebase.
- **10/20 attempt budget.** Implemented in `ExecutionEngine.HandleThresholdAsync`: at phase
  exhaustion → Frontier `RepairRequest` (feedback tail + sanitised diff + audit tail),
  counter reset, advice appended to coder context; at total exhaustion → `BLOCKED`,
  audit event, TUI "ACTION REQUIRED". Both thresholds scale from config (tests run them
  at 2–3 to keep suites fast).

---

## 4. Decisions I had to make on my own (spec gaps)

The specification fixes the loop but leaves operational semantics open. These were
resolved independently:

1. **Pause/Stop = cooperative safe-point exit, not an idling daemon.**
   The engine checks pause/stop flags between attempts; on pause it resets the WP to
   `PENDING`, persists state, and the daemon *exits its run loop*. Resume simply starts a
   new run. Rationale: no long-lived idle process to supervise, no second shutdown path,
   and state-on-disk is the single source of truth. Cost: `start` is re-invoked after
   resume – acceptable for a supervisor workflow.

2. **Runtime truth vs graph truth.** After a completed headless run, `status` initially
   showed everything `PENDING` – `plan.json` is a static artefact while statuses live in
   `state.json.queue_status`. Resolution: all renderers merge the two
   (`queue_status[id] ?? wp.Status`), keeping `plan.json` pristine as *the graph* and
   `state.json` as *the runtime*. Documented rather than hidden.

3. **Volatile runtime files stay out of git.** The audit log is appended constantly and
   `state.json` is rewritten mid-WP; tracking them dirties the tree, and a dirty tree
   halts execution (clean-tree discipline). Resolution: `.tenninety/.gitignore` excludes
   `state.json` + `audit-log.jsonl`; `plan.json`, `config.json`, `spec.md` remain tracked.
   This is a conscious softening of "all state is tracked in Git" in favor of making the
   clean-tree guarantee enforceable; the trade-off is recorded here.

4. **Coder protocol: delegate edits to a terminal coding agent.** Aider, OpenCode, or Pi
   receives the job context and edits the workspace directly; the framework disables each
   tool's auto-commit behavior and owns the resulting Git commit. This replaced the earlier
   hand-rolled full-file JSON writer so established agent tooling handles file edits.

5. **Mechanical tester fails closed in live mode.** Live execution requires a discovered
   test project, a non-empty command, and evidence that at least one test ran; otherwise the
   gate fails. Mock mode may simulate a pass so rehearsal WPs can exercise the orchestration
   loop without generating a real solution. Mock failure windows exist solely for retry tests.

6. **Revert scope.** Part IV.5 wants Frontier analysis → patch application → validation.
   v3.2 implements the *mechanical* path fully (`hotfix/revert-*` branch, `git revert`,
   mechanical tests, one squash commit on pass). When the Frontier says a mechanical revert is
   insufficient, the service refuses and hands back to the human rather than letting an
   LLM freehand a hotfix onto `main`.

7. **Mock provider as a first-class citizen.** Phase 1's exit criterion ("simulate queue
   execution without models") generalizes: `provider_mode=mock` powers demos, CI, and most
   unit tests. Its knobs (`reviewer_fail_attempts`, `reviewer_ignores_advice`,
   `tester_fail_attempts`) exist because each was needed to reproduce a specific loop path
   end-to-end without network.

8. **Natural ID ordering.** `WP-2` sorts before `WP-10` via numeric-suffix extraction;
   lexicographic order would interleave wrongly and scheduling order is user-visible.

9. **Exit-code contract.** `0` success/paused/stopped, `1` error, `2` usage, `4` deadlock –
   so CI can distinguish "finished" from "needs a human".

10. **Secrets policy mechanics.** Framework credentials live only in environment variables
     (`TENNINETY_FRONTIER_API_KEY`, `TENNINETY_LOCAL_API_KEY`); config.json stores only the
     *names* of the env vars. Framework-built prompts pass through `Sanitizer.SanitizeText`,
     and newly added secret-shaped files are unstaged and repo-locally ignored. Coding-agent
     CLIs still have workspace read access, so this is defense in depth, not containment.

11. **Audit taxonomy.** Fixed vocabulary (`WP_STARTED`, `REVIEW_FAILED`, `TESTS_FAILED`,
    `ESCALATION_ADVICE`, `PIVOT_APPLIED`, `REVERT_*`, …) because pivots feed audit tails
    back to the Frontier – free-form strings there would degrade prompt quality.

---

## 5. Defects found during verification – and how I chose to fix them

All were caught either by smoke-testing the real binary or by the test suite; none were
known at design time.

| # | Symptom | Root cause | Resolution |
|---|---|---|---|
| 1 | First WP crashed: `rev-parse --verify` exited 1 | `BranchExists` used the throwing `Run` helper for a probe whose nonzero exit *is* the answer | Probes use `TryRun`; `Run` reserved for commands that must succeed |
| 2 | Audit log unparseable by JSONL consumers (stray BOM, multi-line entries) | Used the pretty-printed serializer and BOM-emitting UTF-8 for an append-only line format | Dedicated compact `JsonSerializerOptions` + `UTF8Encoding(false)`; test parses every line |
| 3 | Resume after stop died: `branch 'work/WP-001' already exists` | Interrupted runs leave their work branch behind; engine demanded fresh branches | Engine reuses an existing work branch (resumed attempts build on prior commits); plus a defensive *WIP checkpoint* commit if a crashed attempt left dirty files |
| 4 | Sanitizer excluded **every** file (`Program.cs` → excluded!) | Pattern `*secret*` split into prefix `""` / suffix `""`; `EndsWith("")` is always true | Proper anchored-glob regex matcher (`*` → `.*`); regression table added including `id_rsa.pub`, `src/x/Program.cs` |
| 5 | `status` lied after a successful run | Renderer trusted stale `plan.json` statuses | Effective-status merge from `queue_status` (see §4.2) |
| 6 | Spectre markup crashes (`Could not find color or style 'P'`) | `[ACTIVE]`, `[P]` etc. parsed as style tags | `Markup.Escape` on all interpolated content; literal brackets escaped `[[…]]` |
| 7 | Compile error in sanitiser redaction | `m.Value[..a - b]` parsed as `Range.All - int` (precedence) | Parenthesized slice bound |
| 8 | Compile error in layer check | Used `dep.Id` where `dep` is already a string id | Use the string directly |
| 9 | Frontier/local HTTP calls brittle | `JsonContent.Create` overload resolution kept binding to the `(object, Type)` form | Explicit `StringContent` with UTF-8 + media type on both call sites |
| 10 | Pivot silently ignored bogus ids | Unknown `keep` entries filtered out instead of rejected | `Apply` now throws on any keep/rework/cancel id that doesn't resolve; DAG re-validation unchanged |
| 11 | Mock reviewer couldn't be made to fail forever | Advice-presence short-circuit overrode the fail threshold | Added explicit `reviewer_ignores_advice` knob rather than overloading magic threshold values |
| 12 | Scripted-reviewer tests broke once escalation reset counters | Fakes keyed off phase counters, which reset | Fakes key off accumulated feedback count – deterministic across resets; mirrors how real reviewers see history |
| 13 | xUnit analyzer warnings (2013/2031) | Count/collection assertions | Switched to `Assert.Empty` / filtering `Assert.Single` overload |

A pattern worth naming: bugs #2, #4 and #12 were all *format-of-intermediate-artefact*
mistakes (JSONL shape, glob semantics, counter-vs-history). The fix in each case was to
make the artefact's contract explicit and pin it with a test, not to patch call sites.

---

## 6. Test strategy

1,000+ tests, all green; release build warning-free (the Docker integration categories are
discovered but skipped until their documented opt-in environment variables are provided).

- **PlanValidatorTests** – acceptance rules of Part VIII as executable checks (cycles,
  dangling deps, dupes, schema pin, atomicity, natural topological order).
- **SanitizerAndJsonTests** – security redaction patterns and fenced/prose JSON recovery,
  including negative cases.
- **StoreTests** – the wire-format contract: literal spec-shaped keys, state round-trips,
  config defaults, true one-line-per-event JSONL.
- **GitServiceTests** – real `git` in temp repos: squash promotion,
  revert-without-rewrite, branch safety, ref resolution.
- **ExecutionEngineTests** – scripted fakes drive the loop: review-fail feedback reaches
  the next coder attempt; tester logs reach coder context; escalation fires exactly once
  and injects advice; total-exhaustion blocks with `ACTION REQUIRED`; pause consumes zero
  attempts.
- **Orchestrator/PivotServiceTests** – ready-selection ordering, blocked-dependency
  starvation, pivot mutations (rework resets budgets, cancel terminal, new WPs validated),
  rejection of DAG-breaking and unknown-id pivots.

The harness mirrors production layout (temp repo + ignored `.tenninety/`) so engine tests
exercise the same clean-tree discipline the daemon enforces.

---

## 7. Known limitations (deferred deliberately)

- **Parallelism**: the knob exists, serial is enforced – the scheduler seam is ready, the
  merge-conflict story is not.
- **Non-mechanical reverts**: require human hands; the framework detects and refuses, it
  does not yet drive a coder through a manual patch.
- **TUI**: requires a real TTY; redirected sessions fall back to headless logging by design.
- **Single machine, single remote-less git**: no push/pull orchestration in v3.2.
- **Docker is part of the trusted computing base**: role containers are isolated from the
  authoritative repository, but daemon compromise and same-user host mutation are outside the
  in-process guarantees. `unsafe-host` remains an explicit non-isolated compatibility mode.
