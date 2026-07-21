# Scaling 10/90 .NET to Medium Projects
**Extension design document – intended location: `docs/scaling-to-medium-projects.md`**

This document extends the 10/90 framework from single-purpose tools to medium-sized systems. The reference workload throughout is a ticketing system for a ~50-person company: 20–40 modules, a relational database, email, authentication, a web UI, and low tens of thousands of lines of C#.

> **Status: design document, not installed behaviour.** `SETUP_GUIDE.md` and `WORKING_GUIDE.md` document only what the starter kit does today, and every command in them is verified against the shipped scripts. Nothing in *this* document is implemented yet. Each extension below states exactly which files and scripts must change. When you implement one, move its instructions into the appropriate guide **in the same commit**. Move operational extensions, such as sharding, test tiers, and the loop, into `WORKING_GUIDE.md`. Move machine-level pieces, such as E3's `ui-test-runner` image, into `SETUP_GUIDE.md`. This applies the framework's own spec-and-code-move-together policy to its documentation. Until then, do not follow this document as a setup guide.

## Why the framework strains at this size – and why it doesn't break

The four-layer architecture, the container boundaries, and the deterministic gate all survive a medium project unchanged. What strains are four specific pressure points: the single-file specification collides with the 65,536-token context window; the test tier has a compiler but none of the services (database, mail, auth) where a real system's riskiest code lives; the web UI is visual and behavioural, which the reflection-and-fixture gate cannot see; and thirty modules of serial local inference make throughput and frontier budget planning questions rather than afterthoughts. Each pressure point gets an extension below (E1–E4), and every extension follows the framework's founding rule: **no constraint exists only as prose in a prompt; every rule an agent might violate is paired with a control that operates below the agent's permission level.**

---

## E1 – Spec sharding

### The problem

Today the entire specification lives in `.cline/rules/architecture.md`, and every Coder invocation carries the whole file in context alongside the skill files, the task, and injected failure logs. A fully-determined spec for 30 modules can run 5,000–15,000 lines. Past a point it does not fit in 65,536 tokens; well before that point it dilutes the model's attention with 29 modules' worth of detail irrelevant to the one being implemented.

### The design

Split the specification along the seams it already has, namely the modules:

```
~/project/workspace/
└── .cline/rules/
    ├── architecture.md            # SHRINKS: global index only (see below)
    └── specs/
        ├── ticket-assignment.md   # one canonical shard per module
        ├── email-notifications.md
        └── ...
```

`.cline/rules/architecture.md` retains only what every module genuinely needs: project-wide invariants and conventions, the full data model, the dependency-ordered build sequence, the interface change policy (verbatim, unchanged), and a **module manifest**, which is a table mapping each module to its shard file, its source paths, and its direct dependencies:

```markdown
## Module manifest
| Module | Shard | Source paths | Depends on |
|---|---|---|---|
| ticket-assignment | specs/ticket-assignment.md | src/[ProjectName]/Assignment/ | data-models, users |
```

Each shard carries the per-module detail the working guide's Appendix A currently demands globally: exact public signatures with overloads, worked examples, edge cases, and a one-line `Depends: data-models, users` header the orchestrator can parse.

### Mechanical enforcement – what must change, file by file

**`check_signatures.csx` pre-commit invocation.** Today a signature change anywhere in `src/` requires `.cline/rules/architecture.md` in the same commit. With sharding, resolve the changed file's path through the module manifest and require **the matching canonical shard** in the same commit instead. This is strictly sharper enforcement: a change to `Assignment/` now points at exactly one file that must move with it.

**`dev.sh` (`cmd_write`, `cmd_review`, `cmd_iterate`)**, instead of mounting the whole workspace's spec context implicitly, construct each agent invocation's visible spec set as: `.cline/rules/architecture.md` + the target module's shard + the shards named in its `Depends:` header (one level deep, transitive closure defeats the purpose). Everything else under `.cline/rules/specs/` is simply not mounted. The agent sees everything relevant and nothing else.

**Freeze policy.** Extend Phase 10.2's audit-trail rule per shard: at each module's first `finalise`, snapshot `.cline/rules/specs/<m>.original.md`, `chmod 444`. The global `.cline/rules/architecture.original.md` rule is unchanged.

**Frontier authoring (Appendix A delta).** The blueprint prompt's output contract changes from "one architecture body" to "one global index conforming to the manifest schema above, plus one shard per module with a `Depends:` header." The frontier model still writes the whole specification once; it delivers it pre-sliced.

### Verification (once implemented)

- [ ] a signature change in a module's source path is blocked unless that module's canonical shard is staged; staging only `.cline/rules/architecture.md` is no longer sufficient
- [ ] a Coder invocation for module A cannot read module B's shard unless B appears in A's `Depends:` header (verify with a deliberate cross-reference in a test prompt)
- [ ] token count of a worst-case invocation (global + largest shard + its dependencies + skills + a long failure log) stays under ~50k, leaving headroom in the 65,536 window

---

## E2 – An integration-real test tier

### The problem

`test-runner` contains the .NET SDK and nothing else. A ticketing system's riskiest code, repository queries, transactional state transitions, email dispatch, auth handshakes, is exactly the code local models get subtly wrong, and the current slow tier has no Postgres, no SMTP endpoint, and no identity provider to test it against.

### The design: host-driven service containers, not Testcontainers

The obvious library answer, Testcontainers, requires handing `test-runner` the Docker socket, which would let test code start arbitrary containers and would breach the framework's central boundary. Keep the container pure and put the orchestration where it already lives: on the host. Add a `compose.integration.yaml` **beside the Dockerfiles in `~/project`, outside the agent-visible workspace**, defining Postgres and a dev mail sink (e.g. smtp4dev), and extend `run_integration_tests.sh` to: bring the compose stack up with per-run generated throwaway credentials, run `test-runner` joined to the compose network with those credentials injected as environment variables, tear the stack down, and treat the container exit code as authoritative exactly as today. Agents never see the compose file, the socket, or any durable credential; connection details exist only for the lifetime of a single host-driven test run.

### Three governance additions that ride along

**A fakes policy in the spec.** The frontier model must name the approved test doubles per external dependency ("repositories are tested against real Postgres in the integration tier; the mail sender is faked with the approved `IMailSender` stub in the unit tier; never mock `DbContext`"). Like the NuGet list, this is closed at the tooling level: any double not in `Directory.Packages.props` cannot restore.

**Authorisation contract tests.** Endpoint protection is a signature-shaped property, so pin it the way the framework pins overloads, using reflection over the endpoint surface asserting that every route in the spec's authorisation matrix carries the required attribute:

```csharp
[Fact]
[Trait("Category", "Contract")]
public void CloseTicket_RequiresAgentRole()
{
    MethodInfo? endpoint = typeof(TicketEndpoints).GetMethod(
        "CloseTicket", BindingFlags.Public | BindingFlags.Static);
    Assert.NotNull(endpoint);
    var auth = endpoint!.GetCustomAttribute<AuthorizeAttribute>();
    Assert.NotNull(auth);
    Assert.Equal("Agent", auth!.Roles);
}
```

These live in the Contracts project and inherit its full protection: write-once staging, `chmod 444`, mounted `:ro` on every agent invocation. The spec gains an **authorisation matrix** section (role × endpoint) that these tests transcribe. An agent cannot quietly drop an `[Authorize]` attribute any more than it can change a return type.

**Migrations are host-generated, never agent-generated.** EF Core migrations are machine-generated code that would flood the signature-drift hook with noise and are a classic place for a model to "fix" a failing test by rewriting history. Add a `dev.sh migrate <name>` subcommand that runs `dotnet ef migrations add` inside `test-runner` (the only image with the SDK), driven by the human; mount `src/**/Migrations/` `:ro` to agent containers; exclude that path in `check_signatures.csx`. The rule "agents write model classes, the host derives migrations from them" keeps the schema's provenance auditable.

### Verification (once implemented)

- [ ] `run_integration_tests.sh` brings the compose stack up, runs the tier, tears it down, and the pass/fail decision is the container exit code
- [ ] credentials injected into `test-runner` differ between two consecutive runs (per-run generation confirmed)
- [ ] the agent container cannot resolve or reach the compose network's services
- [ ] the authorisation contract test fails when an `[Authorize]` attribute is removed from a specced endpoint
- [ ] `dev.sh iterate` on a module whose diff touches `Migrations/` is impossible; the mount is read-only to the agent

---

## E3 – Covering the UI

### The problem, stated honestly

The entire gate rests on deterministic, machine-checkable surfaces, and UI correctness is visual and behavioural: a 24B text model reviewing a diff cannot see a rendered page, and a golden fixture cannot assert that a layout is usable. No extension fully closes this. The goal is to contain the uncovered surface, move the creative work to the layer of the stack that's paid to do creative work, and mechanise what *can* be mechanised.

### Layer one – containment: the API contract is the hard boundary

Architect the UI as a deliberately thin layer (server-rendered Blazor or equivalent) over a fully-gated API, so all correctness-critical logic stays behind the existing gate and the UI carries only presentational risk. Make the boundary itself enforceable: extend `finalise` to export `openapi.json` from the built API and diff it against a frozen copy. This creates an API-shape drift check in the same spirit as `check_signatures.csx`, catching contract changes that reflection over C# alone can miss (routes, status codes, wire-level schemas).

### Layer two – Phase 10b: a frontier-authored design system

UI design is ambiguity-heavy creative work, which by the framework's own logic belongs in the 10%, not the 90%. Add a second one-time authoring pass in which the frontier model produces: a component library (tokens, layout primitives, the ~15 composite components a ticketing system needs), page templates for each screen in the spec, and a new skill file `ui.md` whose core is an **approved-component inventory**, the exact analogue of the approved-package list. Local Coders then only *instantiate* pre-approved components with data, which is mechanical work they are good at; inventing layout, spacing, or interaction patterns is out of their hands by construction.

### Layer three – mechanised regression gating

Add a third image, `ui-test-runner`, Playwright plus browsers, **no LLM tooling, no credentials**, same non-root UID mapping, and three checks to the slow tier: Playwright behavioural flows scripted from the spec's screen definitions ("submitting the new-ticket form lands on the detail page showing status *New*"), golden screenshots frozen `chmod 444` and mounted `:ro` with a pixel-diff threshold, and axe-core accessibility assertions. Be clear-eyed about what screenshot goldens buy: they catch **regressions**, not bad initial design. The first render of every screen is approved by a human, which slots into the machinery that already exists: `finalise` attaches rendered screenshots to the module's `REVIEW_QUEUE.md` entry, and `dev.sh approve` freezes them as the goldens.

### The line that must not move

A vision-capable model can be added as an *advisory* reviewer commenting on rendered screens, but its output feeds the human review queue, never the gate. "Zero AI at test runtime" is the property every other guarantee in this framework rests on; the UI extension bends the framework towards pixels without bending that rule.

### Verification (once implemented)

- [ ] a deliberate CSS change beyond the pixel threshold fails the golden-screenshot check; re-approval via `dev.sh approve` refreezes the goldens
- [ ] a Coder prompt asking for a component not in `ui.md`'s inventory is flagged by the Reviewer checklist ("OUTSIDE INVENTORY: <component>")
- [ ] `ui-test-runner` has no route to llama-swap and no credentials in its environment
- [ ] removing a route from the API fails the `openapi.json` drift check even when no C# signature changed

---

## E4 – Throughput planning and the honest ratio

Plan for the arithmetic rather than discovering it. On one 24 GB GPU running quantised 24–27B models serially through llama-swap, a full Write → Review → Test attempt plausibly costs 10–30 minutes; a 30-module project at 1–3 attempts per module is **days to a couple of weeks of mostly unattended wall-clock**, and the human, reviewing the queue, revising shards, is the real bottleneck. Three practices keep it smooth: batch overnight (queue several `iterate` runs against the dependency order and review each morning); use `dev.sh broadcast` aggressively the first time a cross-cutting pattern is corrected, so the fix propagates to every subsequent Coder run instead of being re-litigated per module; and treat a module's third rejection as a spec defect, not a model defect; this is the working guide's Phase 12.3 rule, which matters far more at 30 modules than at 5.

Expect the effective compute split to drift towards **20/80 rather than 10/90**: sharded spec authoring, the Phase 10b design pass, and mid-project shard revisions all draw frontier tokens. That is not a failure of the model. The framework's own escalation philosophy says frontier spend should track genuine ambiguity, and a real company system simply contains more of it than a single-purpose tool. Budget accordingly and measure: `.escalations.json` already gives you the per-module escalation count; a module needing frequent frontier attention is a shard that was under-specified.

---

## E5 – Migration policy for this document

The rule, restated as a checklist to run whenever an extension lands:

- [ ] the implementing commit changes the scripts/manifests **and** moves the corresponding instructions from this document into the appropriate guide (`WORKING_GUIDE.md` for operational changes, `SETUP_GUIDE.md` for machine-level ones)
- [ ] the moved instructions are rewritten as verified imperative steps (this document's design prose does not transplant as-is)
- [ ] the relevant items from the extension's verification list above are merged into the working guide's Phase 13 checklist (or the setup guide's checklist, for machine-level items)
- [ ] the section here is replaced by a one-line pointer to its new home in the relevant guide
- [ ] the "What is in this repository" description of `docs/` is updated the first time any of this ships

---

## Appendix A′ – Deltas to the frontier blueprint prompt

When implementing E1–E3, amend the working guide's Appendix A prompt output contract as follows. Output item 1 becomes: *"a global index conforming to the module-manifest schema, plus one shard per module, each opening with a `Depends:` header listing direct dependencies only."* Add to the required architecture content: *an authorisation matrix (role × endpoint) precise enough to transcribe directly into reflection contract tests*, and *a fakes policy naming the approved test double for every external dependency, per test tier*. Add two output items: *a design system (tokens, component library, page templates) for every screen in the spec*, and *`ui.md`, a skill file whose approved-component inventory is exhaustive, since local models will be technically limited to it*. The instruction that everything be "mechanically enforced, not requested" already in the prompt is doing the real work; these deltas only widen what it applies to.
