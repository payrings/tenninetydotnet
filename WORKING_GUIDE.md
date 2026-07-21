# 10/90 .NET – Working Guide
## Per-project operations (Phases 9–13)

This guide covers everything you repeat **for each project** built with the 10/90 framework: scaffolding the solution, installing the frontier-authored specification, running the module loop, and reviewing the queue. It assumes the machine has already passed the **setup verification checklist** at the end of `SETUP_GUIDE.md` (Phases 0–8). That checklist covers the GPU runtime, `llama-swap` serving both models, both Docker images, both Cline profiles, frontier access, and host tooling. Nothing from that guide is repeated per project.

Phase numbering continues from the setup guide (starting at 9) so cross-references from other documents remain stable.

### Path conventions

| Path | Meaning |
|---|---|
| `~/tenninetydotnet` | Your local clone of the framework repository. All `cp starter-kit/...` commands are run **from this directory**. |
| `~/project` | This project's parent directory – use a distinct one per project (e.g. `~/tickets`, `~/inventory`). |
| `~/project/workspace` | The project workspace – the directory the agents see, and the root of this project's Git repository. |

**A note on shells:** as in the setup guide, blocks fenced as `fish` use Fish-only builtins and will not work in Bash; blocks fenced as `bash` use `$(...)` syntax and run identically in both.

## Adapting the framework to your project

Replace `[ProjectName]` throughout the documentation and starter-kit manifests with your actual project name in PascalCase with no spaces (for example, `TaskTracker` or `InventoryManager`). Replace `~/project` with your preferred local workspace path. The orchestration mechanics, container boundaries, and governance rules apply unchanged regardless of the business logic you are building.

---

## Phase 9 – Scaffold the project

```
~/project/workspace/
├── REVIEW_QUEUE.md                    # Phase 9.3 – consumed by Phases 11–12
├── BROADCAST.md                       # Phase 11 – broadcast notes to Coders
├── review-feedback/                   # Phase 9.3 – populated as modules get rejected (Phase 12)
├── .cline/
│   ├── rules/
│   │   ├── architecture.md            # Phase 10 – full blueprint + interface change policy
│   │   └── architecture.original.md   # Phase 10 – frozen audit-trail copy
│   └── skills/
│       ├── coder.md                   # Phase 10
│       ├── reviewer.md                # Phase 10
│       └── tester.md                  # Phase 10
├── [ProjectName].slnx                 # .NET solution file
├── global.json                        # Pins the SDK to the .NET 10 floor (Phase 9.1)
├── Directory.Build.props              # Shared build properties (nullable, analysers)
├── Directory.Packages.props           # Central NuGet package management
├── src/
│   └── [ProjectName]/
│       ├── [ProjectName].csproj       # Main project file
│       └── *.cs                       # Implementation files
├── tests/
│   ├── [ProjectName].Contracts/       # Contract tests (staged, frozen read-only)
│   │   ├── [ProjectName].Contracts.csproj
│   │   └── *Tests.cs
│   ├── [ProjectName].Golden/          # Golden-fixture harness (staged, frozen read-only)
│   │   ├── [ProjectName].Golden.csproj
│   │   └── CriticalLogicGoldenTests.cs
│   ├── [ProjectName].Unit/            # Unit tests (fast tier)
│   │   ├── [ProjectName].Unit.csproj
│   │   └── *Tests.cs
│   ├── [ProjectName].Integration/     # Integration tests (slow tier)
│   │   ├── [ProjectName].Integration.csproj
│   │   └── *Tests.cs
│   └── fixtures/
│       └── critical_logic_golden.json # Phase 10 – frontier-authored, read-only
├── scripts/
│   ├── dev.sh                         # The orchestrator (owns all Git state)
│   ├── escalate.py                    # Frontier escalation (host-side Python)
│   ├── check_signatures.csx           # Public-API signature-drift detection (C# script)
│   ├── check_interface_drift.sh       # Downstream drift propagation
│   ├── find_consumers.sh
│   ├── run_tests_with_cascade_check.sh
│   ├── run_integration_tests.sh
│   ├── queue_for_review.sh
│   └── apply_review_feedback.sh
├── .dev-runtime/                      # Per-module runtime artefacts (git-ignored)
├── .pre-commit-config.yaml
└── .gitignore
```

### 9.1 – Create the .NET solution structure

Replace `[ProjectName]` with your actual PascalCase project name (for example, `TaskTracker` or `InventoryManager`). Create the workspace and initialise its Git repository. This is the one and only `git init` per project, and everything from the pre-commit hooks to the orchestrator's tags builds on it. Then create the .NET structure:

```fish
mkdir -p ~/project/workspace
cd ~/project/workspace
git init -q

# Pin the SDK to .NET 10 BEFORE any dotnet command runs, so scaffolding fails
# loudly on a wrong-version host instead of silently targeting it. global.json
# sets a 10.0.0 floor with rollForward: latestFeature – it accepts any 10.0.x
# feature band but refuses .NET 9 or .NET 11, keeping the host aligned with the
# net10.0 target in Directory.Build.props and the test-runner image.
cp ~/tenninetydotnet/starter-kit/global.json ~/project/workspace/

# Create the .NET solution
dotnet new sln -n [ProjectName] --format slnx

# Create main project targeting .NET 10
dotnet new console -f net10.0 -o src/[ProjectName] -n [ProjectName]
dotnet sln add src/[ProjectName]/[ProjectName].csproj

# Create test projects targeting .NET 10
dotnet new xunit -f net10.0 -o tests/[ProjectName].Contracts -n [ProjectName].Contracts
dotnet new xunit -f net10.0 -o tests/[ProjectName].Golden -n [ProjectName].Golden
dotnet new xunit -f net10.0 -o tests/[ProjectName].Unit -n [ProjectName].Unit
dotnet new xunit -f net10.0 -o tests/[ProjectName].Integration -n [ProjectName].Integration

dotnet sln add tests/[ProjectName].Contracts/[ProjectName].Contracts.csproj
dotnet sln add tests/[ProjectName].Golden/[ProjectName].Golden.csproj
dotnet sln add tests/[ProjectName].Unit/[ProjectName].Unit.csproj
dotnet sln add tests/[ProjectName].Integration/[ProjectName].Integration.csproj

# Add project references (test projects reference main project)
dotnet add tests/[ProjectName].Contracts/[ProjectName].Contracts.csproj reference src/[ProjectName]/[ProjectName].csproj
dotnet add tests/[ProjectName].Golden/[ProjectName].Golden.csproj reference src/[ProjectName]/[ProjectName].csproj
dotnet add tests/[ProjectName].Unit/[ProjectName].Unit.csproj reference src/[ProjectName]/[ProjectName].csproj
dotnet add tests/[ProjectName].Integration/[ProjectName].Integration.csproj reference src/[ProjectName]/[ProjectName].csproj

# Create fixtures directory
mkdir -p tests/fixtures
```

Delete the template `UnitTest1.cs` from all four test projects:

```fish
find tests -name UnitTest1.cs -delete
```

### 9.2 – Copy project manifests and build rules

Copy the central build properties, package management configuration, and Git exclusion rules from the starter kit:

```fish
cd ~/tenninetydotnet
cp starter-kit/Directory.Packages.props ~/project/workspace/
cp starter-kit/Directory.Build.props ~/project/workspace/
cp starter-kit/.gitignore ~/project/workspace/
```

(`global.json` was already copied in Phase 9.1, before the solution was
scaffolded, so it is not repeated here.)

The xUnit project template adds `coverlet.collector` to every test project. This framework does not use code-coverage collection, and `Directory.Packages.props` does not define a central version for it. Remove the reference from all four test `.csproj` files:

```fish
cd ~/project/workspace

find tests -name '*.csproj' -exec sed -i \
  '/PackageReference Include="coverlet.collector"/d' {} +
```

Verify that no references remain:

```fish
rg 'coverlet\.collector' tests
```

The command should print nothing.

Ensure the remaining `PackageReference` entries do not contain explicit `Version` attributes, because package versions are managed centrally by `Directory.Packages.props`.

For `tests/[ProjectName].Golden/[ProjectName].Golden.csproj`, retain the link to the golden test fixture:

```xml
<ItemGroup>
  <None Include="../fixtures/critical_logic_golden.json"
        Link="critical_logic_golden.json"
        CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

The fixture does not exist yet; it is created in Phase 10. Referencing it now does not prevent restore or build.

Generate the package lockfiles and stage them:

```fish
cd ~/project/workspace

dotnet restore --force-evaluate

find src tests -name 'packages.lock.json' -print
find src tests -name 'packages.lock.json' -type f -exec git add -- {} +
```
The first `find` command must list one `packages.lock.json` file beside every `.csproj`. If it prints nothing, stop and verify that `Directory.Build.props` contains `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`.

### 9.3 – Install orchestration scripts and pre-commit hooks

Copy the full suite of host-side automation scripts, governance rules, and workspace tracking files from the starter kit into your working directory:

```bash
cd ~/tenninetydotnet
mkdir -p ~/project/workspace/scripts
cp starter-kit/scripts/* ~/project/workspace/scripts/
chmod +x ~/project/workspace/scripts/*
# The canonical golden harness is shipped, pre-tested, in the starter kit and
# instantiated by `dev.sh write-golden-harness`; it is NOT agent-authored.
mkdir -p ~/project/workspace/scripts/golden-harness
cp starter-kit/tests/golden-harness/CriticalLogicGoldenTests.cs.template \
   ~/project/workspace/scripts/golden-harness/
cp starter-kit/.pre-commit-config.yaml ~/project/workspace/
cp starter-kit/BROADCAST.md ~/project/workspace/
cp starter-kit/REVIEW_QUEUE.md ~/project/workspace/
mkdir -p ~/project/workspace/review-feedback
```

**`REVIEW_QUEUE.md` must exist with its table header before the first `dev.sh queue` in Phase 11**. `queue_for_review.sh` appends rows to the file and would otherwise create a headerless one. Copying it here, rather than in Phase 12, guarantees the tracking table is well-formed from the first queued module.

The host tooling these hooks depend on (`dotnet-script`, `pre-commit`) was installed once at machine level in Phase 8.4 of the setup guide. What remains per project is putting this project's scripts on your path and wiring the hooks into this repository:

```fish
fish_add_path ~/project/workspace/scripts
cd ~/project/workspace && pre-commit install
```

**Recommended invocation: `scripts/dev.sh` from the workspace root.** Rather than
putting each project's `scripts/` on `$PATH`, run the orchestrator by its
relative path from inside the workspace: `cd ~/project/workspace` then
`scripts/dev.sh <command>`. Every command in this guide works identically that
way, and it removes the multi-project ambiguity described below entirely.

**Multi-project note and the built-in guard:** each copy of `dev.sh` is
self-locating; it always operates on the workspace it lives in, regardless of
your current directory. `fish_add_path` is permanent and cumulative, so if you
keep several projects on this machine and add each `scripts/` to `$PATH`, a bare
`dev.sh` resolves to whichever came first and would otherwise act on **that**
project's workspace, not the one you are standing in. To make that mistake fail
loudly instead of mutating the wrong project, `dev.sh` now **refuses any
state-changing command when your current directory is not inside its own
workspace**, printing the workspace it would have acted on and how to target the
right one. Read-only commands (`help`, `version`, `status`, `notes`,
`show-frontier-fix`) are exempt. If you deliberately want to run it from
elsewhere, set `DEV_ALLOW_ANY_CWD=1` or an explicit `WORKSPACE`. If you still
prefer `$PATH`, keep only the active project's scripts on it
(`set -U fish_user_paths (string match -v '*old-project*' $fish_user_paths)`).

---

## Phase 10 – Generate the blueprint and complete the scaffold

This phase requires a one-time authoring session with your frontier model. Use the blueprint prompt template in **Appendix A** to generate five essential blocks of text:

1. `.cline/rules/architecture.md` – the single architectural specification
2. `.cline/skills/coder.md` – must name every approved NuGet package explicitly (package and version); this becomes `Directory.Packages.props`.
3. `.cline/skills/reviewer.md`
4. `.cline/skills/tester.md`
5. A golden test fixture saved to `tests/fixtures/critical_logic_golden.json`

### 10.1 – Copy the specification templates and populate them

The starter kit ships annotated template versions of every specification file, so you paste the frontier model's output into a pre-structured document rather than starting from a blank editor:

```bash
cd ~/tenninetydotnet
cp -r starter-kit/.cline ~/project/workspace/.cline
```

Now open each file and paste in the corresponding frontier-authored block, following the placeholder comments inside each template:

```bash
cd ~/project/workspace
nano .cline/rules/architecture.md
nano .cline/skills/coder.md
nano .cline/skills/reviewer.md
nano .cline/skills/tester.md
nano tests/fixtures/critical_logic_golden.json
```

Paste the architecture text directly into `.cline/rules/architecture.md`. It is the only architectural source of truth, so there is no duplicate copy and no synchronisation step.

### 10.2 – Freeze the fixture and the audit-trail copies

Lock your golden fixture and create frozen audit-trail copies of your specification:

```bash
chmod 444 ~/project/workspace/tests/fixtures/critical_logic_golden.json
cp ~/project/workspace/.cline/rules/architecture.md ~/project/workspace/.cline/rules/architecture.original.md
chmod 444 ~/project/workspace/.cline/rules/architecture.original.md
```

### 10.3 – Finalise packages and regenerate lockfiles

Update `Directory.Packages.props` with the exact package versions returned by the frontier model in `coder.md`, then regenerate and stage the lockfiles. Package versions must come from the approved package list; do not infer their major versions from the target framework.

```bash
cd ~/project/workspace

dotnet restore --force-evaluate

git add Directory.Packages.props
find src tests -name 'packages.lock.json' -type f -exec git add -- {} +

git diff --cached --check
```

### 10.4 – Run pre-commit checks and create the initial commit

Stage all files, then run every pre-commit hook before creating the commit:

```bash
cd ~/project/workspace

git add -A
pre-commit run --all-files
```

Do not continue until every hook reports `Passed`.

If a hook fails, correct the reported issue, stage the changed files, and rerun the complete hook suite:

```bash
git add -A
pre-commit run --all-files
```

Some hooks may modify files. After all hooks pass, stage everything again and verify the staged changes:

```bash
git add -A
git status --short
git diff --cached --check
```

Create the initial commit:

```bash
git commit -m "Initial scaffold: solution structure, orchestration scripts, frontier blueprint"
```

Confirm that the workspace is clean:

```bash
git status --short
```

The final command should produce no output.

## Phase 11 – Kick off the local loop using the orchestrator

Make sure `llama-swap` is active (`curl -s http://localhost:8090/v1/models`), Docker is up (`docker ps`), and the workspace has a clean tree on top of the Phase 10.4 initial commit (`git status --short` prints nothing).

Each module moves through an automated development loop managed by `dev.sh`. Every step uses the module's **Module ID** from `.cline/rules/architecture.md`. Copy that ID exactly; do not invent a new ID and do not substitute a source filename. Module IDs are lowercase kebab-case identifiers such as `invoice-calculator` or `user-auth`, and one ID may cover several related source files.

### 11.1 – Start a new module

```fish
cd ~/project/workspace
dev.sh start <module-id>
```

### 11.2 – Author the protected tests through staging

Generate the contract tests for the module:

```fish
dev.sh write-contract <module-id>
```

For example:

```fish
dev.sh write-contract money
```

This command runs synchronously. The terminal may remain quiet while the Coder model is downloaded, loaded, or processing the request.

`dev.sh` runs a quick health check against llama-swap before starting the Coder, so an *unreachable* server now fails immediately with a clear message rather than hanging silently — a quiet terminal after that check means the model is genuinely working, not that the server is down. (If your bind address differs from the Phase 4 default, set `LLAMA_SWAP_HOST_URL`; to skip the check, set `DEV_SKIP_PREFLIGHT=1`.)

If you still want to watch the model load in real time, open a second terminal and monitor the Coder:

```fish
curl -Ns http://172.17.0.1:8090/logs/stream/qwen-coder
```

Normal first-run activity can include:

* downloading model files;
* loading model tensors;
* allocating GPU or system memory;
* starting the inference server;
* processing the contract-generation prompt.

Wait for the original `dev.sh write-contract` command to return.

If the log repeatedly reports HTTP 500 responses or contains:

```text
upstream command exited prematurely
```

the model has failed to start. This is not an active download.

Stop the waiting command by pressing `Ctrl+C` once. Then verify the Coder model directly:

```fish
curl -sS http://localhost:8090/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "qwen-coder",
    "messages": [
      {
        "role": "user",
        "content": "Reply with exactly OK."
      }
    ],
    "max_tokens": 8
  }'
```

Do not retry `dev.sh write-contract` until this request returns a normal completion.

Model-startup failures must be corrected in the machine-level configuration:

```text
~/llama-swap/config.yaml
```

After correcting that configuration, restart llama-swap:

```fish
systemctl --user restart llama-swap.service
```

Then rerun the direct Coder smoke test. Once it succeeds, retry:

```fish
dev.sh write-contract <module-id>
```

Do **not** rerun:

```fish
dev.sh start <module-id>
```

The module start tag was already created successfully. Running `write-contract` again after an interrupted or failed model request is sufficient.

The workspace and all existing tests are mounted read-only. The Coder writes generated contract tests into isolated temporary staging. Only after the Coder exits successfully does the host validate the generated files, move them into the Contracts project, and make them read-only with `chmod 444`.

A failed or interrupted Coder run should not install a partial contract test. Check the Contracts project after an interrupted run:

```fish
find tests -path '*.Contracts/*Tests.cs' -print
```

The command is write-once for each generated file. It refuses to overwrite an existing contract test unless that file is manually removed.

Each contract test must be overload-aware and verify the exact documented public API, including:

* public types and constructors;
* method names and overloads;
* generic arity;
* parameter names, types, and order;
* return types and nullability;
* property types;
* static or instance membership.

A bare reflection call such as `GetMethod("Name")` is insufficient where overloads may exist.

When this module owns the correctness-critical logic represented by the frontier fixture, install the deterministic golden harness before implementation:

```fish
dev.sh write-golden-harness
```

This does **not** call a model. It instantiates the framework's canonical,
pre-tested harness (substituting your project name) and freezes it read-only, so
the code that decides whether a golden case passes is never authored by a local
model. There is no Coder log to monitor for this step.

Review the installed harness before continuing.

Confirm that the golden harness:

* loads `critical_logic_golden.json` from `AppContext.BaseDirectory`;
* deserialises and executes every fixture case;
* rejects malformed or duplicate case IDs;
* invokes production logic rather than reproducing expected calculations;
* uses no current time, randomness, network access, LLM, or agent at test runtime.

### 11.3 – Run the full loop

Execute the write, review and fast-test cycle (up to three automated attempts):

```fish
dev.sh iterate <module-id> "Implement the complete <ModuleName> module (Module ID: <module-id>) exactly as defined in .cline/rules/architecture.md. Create or edit only the files listed in that module manifest."
```

This runs the full Write → Review → Test loop, up to 3 attempts:
1. **Write** – Coder invocation with broadcast prefix injected
2. **Review** – Reviewer invocation (read-only workspace); if FAIL, back to Write
3. **Test** – fast tier; if pass, module clears the loop

If all 3 attempts fail, the orchestrator prints explicit numbered choices.

### 11.4 – Finalise, then queue for review and move on

Run slow integration tests and propagate interface drift checks across consuming modules:

```fish
dev.sh finalise <module-id>
dev.sh commit <module-id>
dev.sh queue <module-id>
```

`finalise` runs the slow (integration) tier once and propagates any interface
drift to downstream modules. `commit` is the orchestrator's own Git step; agents never commit. It turns the finalised module into a fixed artefact,
returning the tree to a clean state so the **next** module can start. `queue`
then records the module for human review and folds the queue row into that
commit.

Each step is gated on a **content fingerprint**, not a commit hash: `finalise`
refuses a module that has not passed review and the fast tier, `commit` and
`queue` refuse a module whose code changed since it was finalised, and a module
that was never started cannot be finalised at all. Because agents never commit,
a hash-of-HEAD marker would compare equal to itself forever; the fingerprint
covers tracked, staged and untracked content, so any edit invalidates every gate
the module had already earned.

**Interface changes are gated on you, not the Coder.** The pre-commit
signature-drift hook only checks that `.cline/rules/architecture.md` was edited
in the same diff as a signature change — a condition the Coder can satisfy by
itself. So if a module's diff touches `.cline/rules/architecture.md`, both
`finalise` and `commit` refuse and print the change against the frozen
`architecture.original.md`. Review it, confirm it had the frontier-model review
the interface change policy requires, then re-run with the explicit flag:

```fish
dev.sh finalise <module-id> --allow-spec-change
dev.sh commit <module-id> --allow-spec-change
```

This keeps a human decision between the Coder and any change to the agreed
interface.

### 11.5 – Structured frontier escalation (if needed)

Escalation is tiered and deliberate: the **first** call per module always produces a plan only, and each subsequent tier must be explicitly unlocked with `--override`; the orchestrator will not silently re-escalate on its own. The `--override` flag is only for the deliberate second pass after you've reviewed the first frontier plan:

```fish
# Tier 1 – frontier writes a PLAN (no code, no --override):
dev.sh escalate <module-id> .dev-runtime/<module-id>/latest-test.log
```

If, after applying that plan, the module still fails, escalate deliberately:

```fish
# Tier 2 – a second plan after your review:
dev.sh escalate <module-id> .dev-runtime/<module-id>/latest-test.log --override

# Tier 3 – frontier writes the actual fix code:
dev.sh escalate <module-id> .dev-runtime/<module-id>/latest-test.log --override --write-code
```

To display a frontier fix without applying it automatically:

```fish
dev.sh show-frontier-fix <module-id>
```

After applying the frontier fix:

```fish
dev.sh test <module-id>
```

### 11.6 – Broadcast notes (optional)

To broadcast mandatory architectural patterns to all subsequent local coder runs:

```fish
dev.sh broadcast "When implementing <pattern>, always use <helper>; do not write custom logic inline."
```

Clear it:

```fish
dev.sh broadcast ""
```

---

## Phase 12 – The review queue and human feedback

### 12.1 – Understand the review queue and feedback directory

Both the tracking file and the feedback directory were already installed in Phase 9.3. **Do not copy or recreate them now**: `REVIEW_QUEUE.md` already contains the rows added by every `dev.sh queue` you ran in Phase 11, and overwriting it would erase them. Simply confirm they are in place:

```bash
ls ~/project/workspace/REVIEW_QUEUE.md ~/project/workspace/review-feedback/
```

`REVIEW_QUEUE.md` is the human-facing tracking table that `queue_for_review.sh` appends to. It uses the following structure:

```markdown
# Review queue

Status values: `ready-for-review` (passed all gates, awaiting human),
`needs-fixes` (human rejected), `interface-changed` (a dependency's public API
moved – re-test), `approved` (human approved).

| Module | Status | Times rejected |
|---|---|---|
```

`review-feedback/` stores your rejection notes. `dev.sh reject` writes one file per rejected module here, and `dev.sh fix` reads them back into the local coding loop. It stays empty until the first rejection.

### 12.2 – Reviewing a queued module

Work through `REVIEW_QUEUE.md` whenever you have time, in the build order from `.cline/rules/architecture.md`.

**Satisfied?** Run the approval command:

```fish
dev.sh approve <module-id>
```

**Found a real issue?** Run the rejection command with feedback specific enough for the AI to fix:

```fish
dev.sh reject <module-id> "<what's wrong, specific enough for an AI to fix>"
```

### 12.3 – Automated repair loops

Feed your rejection notes back into the automated local coding loop:

```fish
dev.sh fix <module-id>
```

If a module's "Times rejected" count reaches 3, stop asking for a fourth fix. Go back to Phase 10, revise the relevant section with the frontier model, and only then queue a fresh implementation attempt.

---

## Phase 13 – Per-project verification checklist

Machine-level items (GPU, model serving, images, profiles, frontier connectivity) were verified once by the setup guide's checklist and are not repeated here. This checklist covers what is specific to **this project's** repository, hooks, and orchestration:

- [ ] the workspace repository has an initial commit and a clean tree (`git log --oneline` shows the Phase 10.4 commit; `git status --short` prints nothing)
- [ ] `dotnet build` succeeds with 0 errors, 0 warnings
- [ ] each test project builds and discovers tests: Contracts, Golden, and Unit all run explicitly (not filtered by trait)
- [ ] the golden harness (in the Golden project) executes every case in `critical_logic_golden.json` and fails on a missing entry point or duplicate case ID
- [ ] `dev.sh help` lists `finalise`, `write-golden-harness`, and `show-frontier-fix`
- [ ] `pre-commit` blocks a deliberately-added raw SQL line in a `.cs` file (and ignores `bin/`/`obj/`)
- [ ] `pre-commit` blocks a signature change in `src/` without `.cline/rules/architecture.md` in the same commit
- [ ] `Directory.Packages.props`, Contracts, Golden, fixtures, and the frozen `.original.md` files are all mounted `:ro` to agents; agent-visible `.git` is read-only
- [ ] `run_tests_with_cascade_check.sh` runs `dotnet build`/`dotnet test` with `--network=none` (a test that attempts an outbound connection fails), while `dotnet restore --locked-mode` runs in a separate networked step
- [ ] `run_tests_with_cascade_check.sh` runs from the host inside `test-runner`, treats the container exit code as authoritative, and prints deliberate escalation instructions (it does NOT auto-escalate) when handed an artificially large build error log
- [ ] `run_tests_with_cascade_check.sh` respects `DOTNET_ERROR_THRESHOLD`
- [ ] `cmd_iterate` injects the captured test/review output into the next Coder attempt (verify the failing log text appears in the next prompt)
- [ ] `dev.sh check-coverage` flags any tracked `src/*.cs` file not listed in a module manifest (and passes when every file is covered)
- [ ] two concurrent mutating `dev.sh` commands in one workspace are serialised: the second fails fast with an "already running" message, while read-only commands (`status`, `check-coverage`) still run
- [ ] `dev.sh reset` writes a recoverable backup under `.dev-runtime/reset-backups/` before discarding module work
- [ ] `escalate.py` reads `OPENROUTER_API_KEY` from a mode-600 `.env` (or the environment), warns on loose permissions, and `--dry-run` writes artefacts to a temp dir without touching `.escalations.json`
- [ ] `dev.sh iterate`/`write`/`review`/`write-contract` fail fast with a clear message (not a silent hang) when llama-swap is unreachable, and `DEV_SKIP_PREFLIGHT=1` bypasses the check
- [ ] the human-feedback repair (`dev.sh fix`) cannot proceed past a Reviewer `VERDICT: FAIL` (it delegates to `dev.sh iterate`)
- [ ] `dev.sh` fails an iteration with `OUT-OF-SCOPE FILE(S)` — before invoking the Reviewer — when a module diff touches a path not listed under its manifest's **Implementation files** / **Shared integration files** (and allows `.cline/rules/architecture.md` as the interface-change exception)
- [ ] `dev.sh write-contract` mounts the workspace read-only, accepts one staged `<Type>Tests.cs` file per documented public entry point, and refuses to overwrite an existing contract test
- [ ] re-running `dev.sh write-contract <module-id>` after a second entry point is added to the manifest creates only the new file and leaves existing contract tests untouched
- [ ] a `write-contract` batch containing any already-existing filename is rejected whole, leaving the Contracts project unchanged
- [ ] `dev.sh write-golden-harness` installs the canonical framework harness (not a model-authored file), substitutes the project name, and makes it read-only
- [ ] contract tests use exact overload-aware reflection checks (not a bare `GetMethod(name)`)
- [ ] `find_consumers.sh` correctly lists a known call site for a real symbol in the codebase
- [ ] `check_signatures.csx -- --since module-start-<module-id>` reports an added/removed/overloaded method, a return-type-only change, and a base-list change
- [ ] `check_signatures.csx -- --names-only` emits bare symbol names; `check_interface_drift.sh` marks downstream modules `interface-changed`
- [ ] `OPENROUTER_API_KEY` and `FRONTIER_MODEL` are both set, and `scripts/escalate.py` returns a real response on a dummy diff
- [ ] the FIRST `dev.sh escalate <module-id> <log>` (no `--override`) produces a plan; `--override --write-code` produces fix code saved to `frontier-fix-<module-id>.md`
- [ ] `queue_for_review.sh` correctly adds a new row to `REVIEW_QUEUE.md`
- [ ] `dev.sh finalise <module-id>` refuses a module that was never started, and refuses one that has not passed review and the fast tier
- [ ] `dev.sh finalise`/`commit` refuse a module whose diff edits `.cline/rules/architecture.md`, print the change against `architecture.original.md`, and proceed only with `--allow-spec-change`
- [ ] `dev.sh commit <module-id>` refuses a module whose code changed after `finalise`, and leaves the working tree clean on success
- [ ] `dev.sh queue <module-id>` refuses to run until `dev.sh commit <module-id>` has succeeded for the current content
- [ ] after `queue`, `dev.sh start <next-module-id>` succeeds; the tree is clean and a second module can begin
- [ ] `dev.sh reject <module-id>` increments the "Times rejected" column, and the third rejection prints the revise-the-spec instruction
- [ ] `dev.sh fix <module-id>` exits non-zero when the underlying repair fails
- [ ] `apply_review_feedback.sh` (via `dev.sh fix`) exits 1 when the gate fails all 3 attempts
- [ ] `dev.sh broadcast "<note>"` adds a note, and `dev.sh write` includes it in the task
- [ ] `dev.sh` refuses a state-changing command (e.g. `start`) run from a directory outside its own workspace, names the workspace it would have acted on, and is bypassable with `DEV_ALLOW_ANY_CWD=1`; read-only commands (`version`, `status`) still run from anywhere
- [ ] `dev.sh start <module-id>` fails on a dirty tree, on a missing initial commit, and on an already-started module
- [ ] `dev.sh reset <module-id>` restores tracked files, removes module-created untracked files, and deletes the `module-start-<module-id>` tag so the module can be re-started
- [ ] `.cline/rules/architecture.original.md` exists and is read-only

---

---

## Appendix A: Blueprint prompt template (C# and .NET)

Use this prompt with whichever frontier model you choose to generate the five content blocks. Fill in the bracketed sections with your actual project.

### Frontier blueprint prompt

You are acting as the sole system architect for a small, well-scoped project. Your output will be read by two much smaller local coding models (27B and 24B parameter, not frontier-tier) that will implement it without further architectural input from you. Anything you leave ambiguous, they will each guess independently, and there's no guarantee they'll guess the same way. Your job is to remove every structural decision from their hands, not to describe good practices in the abstract.

**Important context about how your output will be used:**

- The two local models run on local hardware at zero marginal cost. The Coder writes code; the Reviewer checks it against a checklist. A deterministic test suite (`dotnet build -warnaserror` + `dotnet test` with xUnit) is the mechanical gate.
- Your specification will be **mechanically enforced**, not just requested: a pre-commit hook blocks any commit that changes a function or class signature in `src/` without `.cline/rules/architecture.md` being updated in the same diff. The interface-change policy you write in `.cline/rules/architecture.md` is not advisory; it's enforced by tooling.
- The NuGet package list you write in `coder.md` becomes a `Directory.Packages.props` file, and the test container restores with `dotnet restore --locked-mode`, meaning the Coder is *technically incapable* of pulling in anything not on your list, not merely told not to. Every package and version must be named explicitly.
- The `tests/` directories for contracts and fixtures are mounted read-only (`:ro`) on every agent invocation; neither the Coder nor the Reviewer can edit, delete, or create files in them. The golden fixture you produce will be `chmod 444` and mounted read-only, so neither local model can quietly rewrite a test to make broken code pass.
- A frozen audit-trail copy of your output (`.cline/rules/architecture.original.md`) will be kept read-only at project start, so the working specification can always be diffed against your original.
- A `dev.sh` orchestrator handles all agent invocations via short subcommands. The skill files you write should reference the actual workflow, not assume the models will figure out how to run things themselves.

**Project:** [Describe your project in 2–4 sentences, including what it does, who uses it, and the core business outcomes it needs to deliver.]

**Stack:** C# 14 / .NET 10, xUnit for testing, [your database/ORM choice], Roslyn analysers for static analysis, central NuGet package management via Directory.Packages.props.

All projects must target `net10.0`, and the solution must use the `.slnx` format. Do not propose .NET 9 targets or downgrade framework-specific package dependencies.

**Output exactly five things:**

### 1. `.cline/rules/architecture.md` – the single architectural specification

Produce the complete contents of `.cline/rules/architecture.md`. This file is the single source of truth. Include, at the level of detail that removes ambiguity, not summarises it:

- **Full project structure.** List every `.cs` file path that will exist, every `.csproj`, and the `.slnx` file. Do not say "organise this sensibly"; provide the actual tree.
- **A module catalogue and a complete manifest for every module.** A module is the unit passed to `dev.sh`; it may contain one file or several related files. Do not define modules merely as informal headings. Give each module a unique, stable **Module ID** that the human will copy verbatim into every command (`dev.sh start`, `write-contract`, `iterate`, `finalise`, and `queue`). The human must never need to invent an ID.

  Module IDs must:
  - use lowercase kebab-case matching `^[a-z][a-z0-9]*(-[a-z0-9]+)*$`;
  - describe the module, not a particular source filename;
  - remain stable for the life of the project; and
  - be a stable manifest key, not a class name. `dev.sh write-contract <module-id>` looks the ID up in the manifest and derives one contract-test file per documented public entry point (`<TypeName>Tests.cs`), so a module ID need not match any single type. For example, `invoice-calculator` yields `InvoiceCalculatorTests.cs`, plus a second file only if the manifest documents a second entry point.

  Begin with a dependency-ordered catalogue:

  ```markdown
  ## Module catalogue
  | Order | Module | Module ID | Depends on |
  |---:|---|---|---|
  | 1 | Money | `money` | – |
  | 2 | Invoice Model | `invoice-model` | `money` |
  | 3 | Invoice Calculator | `invoice-calculator` | `money`, `invoice-model` |
  ```

  Then define every module using this exact manifest structure:

  ```markdown
  ## Module: Invoice Calculator

  **Module ID:** `invoice-calculator`
  **Purpose:** Calculate invoice subtotal, tax, rounding and final total.
  **Depends on:** `money`, `invoice-model`

  ### Implementation files
  Exact repository-relative paths that the implementation Coder may create or edit:
  - `src/Project/Billing/IInvoiceCalculator.cs`
  - `src/Project/Billing/InvoiceCalculator.cs`
  - `src/Project/Billing/InvoiceResult.cs`

  ### Shared integration files
  Exact repository-relative paths this module may edit even though other modules also use them, together with the permitted change:
  - `src/Project/DependencyInjection.cs` – add only the Invoice Calculator registrations.
  Write `None` when there are no shared integration files.

  ### Protected/generated test artefact
  - `tests/Project.Contracts/InvoiceCalculatorTests.cs` – generated by `dev.sh write-contract invoice-calculator`; implementation agents must not edit it.

  ### Public contract
  Exact C# type, constructor, property and method signatures, with XML documentation. Include overloads, generic arity, parameter names/types/order, return types, nullability and static/instance distinctions.

  ### Required behaviour
  Numbered, deterministic rules covering normal cases, boundaries, errors, rounding, ordering and state changes.

  ### Required contract-test coverage
  A concrete list of signatures and observable behaviours the protected contract test must verify. Describe behaviour, not which production file to test.

  ### Acceptance examples
  Worked inputs and exact outputs, including edge cases.

  ### Out of scope / prohibited changes
  Exact files, modules, public interfaces or behaviours this module must not alter.

  ### Completion criteria
  An objective checklist that tells the Reviewer when the whole module, not merely one source file, is complete.
  ```

  Rules for manifests:
  - List exact paths; do not use broad globs such as `src/**`.
  - Every production file in the project tree must belong to at least one module manifest.
  - A changed path is allowed only when it appears under that module's **Implementation files** or **Shared integration files**. The sole global exception is `.cline/rules/architecture.md` during a deliberate interface change governed by the interface change policy below; do not list the architecture file as ordinary module scope.
  - If a shared file may be touched by several modules, state the permitted edit for each module so unrelated sections remain out of scope.
  - Do not tell the Coder to "implement `<Type>.cs`" when the module owns several files. The module manifest, not the task sentence, defines the complete scope.
  - Mark each module's **public entry points** explicitly. These are the types consumers outside the module call directly. `dev.sh write-contract` derives one `<TypeName>Tests.cs` per entry point, so a type that is only reachable through another entry point must not be marked as one.
- **Each module's single responsibility** and **public interface** must live inside its manifest. C# class/method signatures with full XML doc comments, not implementations. Include exact overloads, generic arity, parameter names/types/order, return types, nullability and static/instance distinctions (the contract tests and signature-drift hook are overload-aware).
- **Dependency direction between modules.** Express dependencies by Module ID and state the global direction explicitly, e.g. "Service modules call repository modules; repository modules never call back up into services."
- **The full data model.** List every `record` or class used as data, every field/property with its type and constraints, every relationship, and the manifest of the module that owns it.
- **An explicit, dependency-ordered build sequence.** Use the Module IDs from the catalogue, state which module must be complete before the next one begins, and explain why.
- **The exact logic for anything correctness-critical.** This may be a calculation, a state machine, a permissions model, or whatever your project's highest-risk deterministic logic is. Spell it out precisely enough that two different models implementing it independently would produce identical behaviour. Include worked numeric or scenario examples, not prose that could be read two ways.
- **Ownership of state.** State where each piece of mutable state lives, what's allowed to change it, and what's read-only downstream.
- **The schema of `critical_logic_golden.json`.** Require stable unique case IDs, explicit typed inputs, and exact expected outputs. A canonical, pre-tested harness shipped with the framework (not agent-authored) loads and executes every case against the documented `entryPoint`.
- **An interface change policy**, stated exactly as follows (this wording is required because a pre-commit hook enforces it mechanically):

  ```markdown
  ## Interface change policy
  Any change to a function or class signature already defined in this
  document requires:
  1. Updating this document in the same diff; spec and code must never drift.
  2. Mandatory frontier-model review of the change, regardless of which
     module it's in or that module's normal risk tier.
  ```

### 2. `.cline/skills/coder.md`

Coding conventions and constraints: naming conventions (PascalCase for public, camelCase for private), where new code must go relative to the layout in `.cline/rules/architecture.md`, **the approved NuGet package list, with every package and version named explicitly** (this becomes `Directory.Packages.props`), and an explicit instruction to implement against the signatures in `.cline/rules/architecture.md` exactly rather than redesigning them.

Also require the Coder to treat the task's Module ID as authoritative: locate that manifest, implement the **complete module**, create or edit only paths listed under its **Implementation files** or **Shared integration files**, and make only the specifically permitted change inside a shared file. It must not infer scope from a filename in the task, create convenience files outside the manifest, or edit another module to make the current one easier. If the task, manifest and existing repository disagree, it must stop and ask a specific clarifying question.

**Must also include these two sections:**

```markdown
## Testing policy
The testing policy is in `.cline/skills/tester.md`. Read and follow it. In
particular: you cannot run tests, `dotnet build`, `dotnet test`, or any
package manager; none of them exist in your sandbox. Do not try. When a
task hands you a test failure log, treat it as ground truth and fix the
code it describes.

## Clarifying questions
If you encounter genuine ambiguity in `.cline/rules/architecture.md`, where the spec doesn't say what to do for a case you actually need to
handle, halt and ask the user a specific clarifying question. Do not
guess. A question costs one turn; a wrong guess costs at least three.
```

### 3. `.cline/skills/reviewer.md`

A checklist a *different* model than the one that wrote the code will use to review it. Write concrete, checkable items such as "does every public method signature match `.cline/rules/architecture.md` exactly," "does any file contain raw SQL," "does the [critical logic] match the worked examples in `.cline/rules/architecture.md`", not general instructions like "check for bugs."

The checklist must make the module manifest operational. Require the Reviewer to:

1. Extract the Module ID from the `module-start-<module-id>` tag named in its task and locate the matching manifest in `.cline/rules/architecture.md`.
2. Run `git diff --name-status module-start-<module-id>` and review **every** added, modified, renamed or deleted file in that diff; it must not review only the file whose name resembles the module.
3. Fail with `OUT-OF-SCOPE FILE: <path>` for any changed path not listed under that manifest's **Implementation files** or **Shared integration files**, except `.cline/rules/architecture.md` during a deliberate interface change. If that exception appears, flag `INTERFACE SPEC CHANGED – frontier review required` and verify the interface change policy is being followed. (Note: `dev.sh` already enforces this scope rule mechanically before the Reviewer runs, so an out-of-scope diff never reaches you; keep this check as a backstop for anything the path-level gate cannot see, such as a permitted shared file edited beyond its allowed change.)
4. For a shared integration file, verify that only the permitted change described by the manifest was made.
5. Verify that every required implementation file, public contract, behaviour, acceptance example and completion criterion in the manifest is satisfied.
6. Treat tests as checks of module behaviour and public contracts, not checks of individual production files.

**Must open with:**

```markdown
## Before you review anything
Run `git diff module-start-<module-id>` in the terminal to see the
actual changes for this module (substitute the real Module ID; fall
back to plain `git diff` if no tag exists). Never assume a file
describing the diff exists; there isn't one. The diff is git output, not
a document.

On the **first iteration** of a new module (when no contract test exists
yet), review the **full module file(s)**, not just the diff. On
subsequent iterations, review the diff plus any code you flagged earlier
that the Coder didn't address.
```

**Must also include these four sections:**

```markdown
## Outside-checklist rule
If you encounter something in the diff that isn't covered by the checklist
above, flag it explicitly rather than silently skipping it. Report
"OUTSIDE CHECKLIST: <what you saw>" as part of your review.

## Test-pass-by-coincidence check
When reviewing a fix for a test failure, check whether the fix addresses
the underlying bug or merely makes the test pass. Red flags:
- Broad exception catches (`catch (Exception)` or `catch`) added around
  the failing code path
- The failing assertion's condition was changed to always be true
- The test was modified (it shouldn't be, because tests are read-only for you,
  in the Contracts project, the Golden project, and tests/fixtures/)
- A mock was added to make the failing call return what the test expected

If the fix makes the test pass without addressing the bug, FAIL the review
with "TEST-PASS-BY-COINCIDENCE: <explanation>."

## Manifest scope
The module manifest in `.cline/rules/architecture.md`, located by the task's
Module ID, is the authoritative scope. Every path in the diff must appear
under that module's **Implementation files** or **Shared integration files**,
and an edit inside a shared file must be the specific change the manifest
permits. The only global exception is `.cline/rules/architecture.md` itself
during a deliberate interface change.

If the diff touches a path the manifest does not list for this module, FAIL
the review with "OUT OF SCOPE: <path>." If a path the manifest lists is
missing from the diff, say so as "INCOMPLETE MODULE: <path>"; the module is
not done until its manifest is satisfied and its completion criteria are met.
A module may legitimately span several files; do not treat a multi-file diff
as suspect in itself.

## Contract tests
On the first iteration of a new module, after reviewing the implementation,
also verify that a contract test exists in the project's Contracts test
project for **every public entry point the manifest documents**. A module
with two entry points has two `<Type>Tests.cs` files, not one. For each entry
point that has no contract test, flag: "CONTRACT TEST MISSING for <Type>."
The human will run `dev.sh write-contract <module-id>` to create the missing
ones; it is safe to re-run, because existing contract tests are never
overwritten.

## Verdict line
End every review with a single final line that is exactly one of:

    VERDICT: PASS
    VERDICT: FAIL

Put all issues and the "OUTSIDE CHECKLIST", "TEST-PASS-BY-COINCIDENCE",
"OUT OF SCOPE", "INCOMPLETE MODULE" and "CONTRACT TEST MISSING" notes on
lines above the verdict. The orchestrator reads only the LAST line that begins
with `VERDICT:` and requires it to be exactly `VERDICT: PASS` (nothing else on
that line) to pass; anything else — a trailing `VERDICT: FAIL`, extra text on
the verdict line, or no verdict at all — is treated as a failure. So put the
verdict on its own final line with nothing after it, and do not write `VERDICT:
PASS`/`VERDICT: FAIL` anywhere else in your review. If anything failed, the
verdict is FAIL.
```

### 4. `.cline/skills/tester.md`

```markdown
## Test routine
Tests are executed on the HOST by a human running test scripts outside
your sandbox. You, the agent, must NEVER attempt to run tests, `dotnet
build`, `dotnet test`, `dotnet restore`, or `dotnet format` yourself:
none of them exist in your environment, and trying will only produce
"command not found" errors.

Tests exercise public contracts and observable module behaviour; they do not select or "test" individual production source files.

Your responsibilities are exactly two:
1. When a task hands you a test failure log, treat it as ground truth and
   fix the code it describes; do not dispute the log or attempt to
   re-run anything.
2. Never edit anything under the Contracts test project
   (`tests/[ProjectName].Contracts/`), the Golden project, or `tests/fixtures/`
   to make a failure go away. Fix the code, not the test.
```

### 5. A golden fixture

A fixed set of input/expected-output cases for the same correctness-critical logic called out in `.cline/rules/architecture.md`; this becomes `tests/fixtures/critical_logic_golden.json`, frozen and read-only (both `chmod 444` and mounted `:ro`).

It is consumed by a deterministic xUnit harness (no LLM at run time). The
harness accepts two shapes; prefer **(A)** whenever the module's
correctness-critical logic has more than one entry point.

**(A) Multiple entry points (preferred):**

```json
{
  "groups": [
    {
      "entryPoint": "Namespace.Type.Method",
      "cases": [
        { "name": "descriptive case name", "input": { "paramName": value }, "expected": value },
        { "name": "rejects negative amount", "input": { "paramName": value }, "expectedError": "ArgumentOutOfRangeException" }
      ]
    },
    { "entryPoint": "Namespace.OtherType.Method", "cases": [ /* ... */ ] }
  ]
}
```

**(B) Single entry point (legacy, still supported):**

```json
{
  "entryPoint": "Namespace.Type.Method",
  "cases": [
    { "name": "descriptive case name", "input": { "paramName": value }, "expected": value }
  ]
}
```

- `entryPoint` is a fully-qualified `Namespace.Type.Method` that exists in the
  public API you specify in `.cline/rules/architecture.md`. It must be a single,
  unambiguous public method (static, or instance on a type with a public
  parameterless constructor). The canonical harness resolves it reflectively; an
  overloaded name fails loudly, so give each golden entry point one unambiguous
  overload.
- Each `input` object's keys match that method's parameter names exactly, and
  every parameter must be supplied. Values are converted to the declared
  parameter types (numbers may be given as JSON numbers or numeric strings).
- Each case gives **exactly one** of `expected` or `expectedError`:
  - `expected` is the method's return value for those inputs. The harness
    compares actual vs expected by canonical (key-sorted, culture-invariant)
    JSON, so numbers, strings, booleans, arrays and object shapes all compare
    exactly; give `expected` in the natural JSON form of the return type.
  - `expectedError` is the simple or fully-qualified name of the exception type
    the entry point must throw for those inputs (e.g. `"ArgumentOutOfRangeException"`).
- Case `name` is the test ID and must be **unique across the whole fixture**,
  not just within a group. `Task`/`Task<T>`/`ValueTask` entry points are awaited
  to completion before the comparison (including for `expectedError`).

The harness itself is shipped and pre-tested by the framework (installed via
`dev.sh write-golden-harness`), so you only author the fixture, not the code
that runs it.

The fixture must be **consistent with the worked examples in `.cline/rules/architecture.md`**. Since you are writing both artefacts, keep them aligned.

---

## Appendix B: Overload-aware C# contract test pattern

A contract test should identify the **exact overload** rather than using a bare method name. Using `GetMethod("Name")` without type parameters throws an `AmbiguousMatchException` when overloads exist, and even when it doesn't, it fails to pin the parameter and return types the spec documents.

```csharp
using System.Reflection;
using Xunit;
using [ProjectName];

namespace [ProjectName].Contracts;

public sealed class ExampleServiceTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void Calculate_String_Int32_ExistsWithExpectedReturnType()
    {
        MethodInfo? method = typeof(ExampleService).GetMethod(
            "Calculate",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(string), typeof(int) },
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(CalculationResult), method!.ReturnType);
        Assert.False(method.IsStatic);
        Assert.Empty(method.GetGenericArguments());
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void Name_HasExpectedTypeAndSetterPolicy()
    {
        PropertyInfo? property = typeof(ExampleService).GetProperty(
            "Name",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod); // documented get-only property
    }
}
```

For generic overloads, select from `GetMethods()` using all of: method name; generic argument count; parameter count and exact parameter types; static/instance status; and return type.

Contract tests check the documented API *shape*. Behavioural correctness belongs in the deterministic Golden, Unit, and Integration projects. Note the `[Trait("Category", "Contract")]` attribute is retained for clarity and ad-hoc filtering, but the gate runs each test project explicitly rather than by trait.

---

## Appendix C: Troubleshooting quick reference (per-project)

For GPU, model-serving, firewall, and container-image symptoms, see the machine-level troubleshooting appendix in the setup guide (`SETUP_GUIDE.md`).

| Symptom                                                                           | Fix                                                                                                                                    |
| --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `fish: Unknown command: dev.sh`                                                   | run `fish_add_path ~/project/workspace/scripts` once (permanent)                                                                       |
| `cp: cannot stat 'starter-kit/...'`                                               | you're not inside your clone of this repo – `cd ~/tenninetydotnet` first (see *Path conventions*)                                      |
| `dev.sh start` refuses to run                                                     | dirty tree or no initial commit – complete Phase 10.4 (`git add -A && git commit`), confirm `git status --short` prints nothing        |
| Cline hangs with no output                                                        | use `-i` not `-it` for captured mode; `-t` forces pseudo-TTY which breaks capture                                                      |
| Wrong model responds                                                              | check which `~/.cline-*` directory got mounted (or use `dev.sh write` / `dev.sh review`)                                               |
| `dev.sh iterate` exits silently after `==> Pass N: WRITE`                         | `set -euo pipefail` killed script on non-zero exit; v3 uses `\|\| true` after capture                                                  |
| A commit is unexpectedly rejected                                                 | check `.pre-commit-config.yaml` – should have `no-raw-sql`, `signature-drift`, `dotnet-format`                            |
| `signature-drift` fails with "package ... was not found in the global NuGet cache(s)" | the `dotnet-script` Roslyn cache was never warmed; run the Phase 8.4 warm step from `SETUP_GUIDE.md` (or `dotnet script --no-cache scripts/check_signatures.csx -- --staged` once, with network access) |
| `dotnet build` fails with " NU1004: The version of package is not defined"        | `Directory.Packages.props` is missing a package version; add it                                                                        |
| `dotnet restore --locked-mode` fails                                              | run `dotnet restore` first to (re)generate `packages.lock.json`, then commit it – required after any `Directory.Packages.props` change |
| A test project reports "EMPTY TEST GATE"                                          | the project built but discovered no tests; check it has a `[Fact]`/`[Theory]` and references the project under test                    |
| `dev.sh write-contract` writes to wrong path or wrong number of files             | the agent derives filenames from the module manifest's public entry points – check the Module ID exists in `.cline/rules/architecture.md` and its entry points are documented |
| Fast test tier is slow every single run                                           | NuGet cache volume may have been removed; first run after `Directory.Packages.props` change is slow (cold cache)                       |
| `escalate.py` produces an empty diff                                              | `module-start-<module-id>` tag was never created – run `dev.sh start <module-id>` first                                                          |
| A module keeps bouncing between `needs-fixes` and `ready-for-review`              | check "Times rejected" – if at 3, revise `.cline/rules/architecture.md` instead                                                                           |
| Downstream modules break after an interface change                                | `check_interface_drift.sh` should have marked them as `interface-changed`                                                              |
| The repo is in a broken state after a crash                                       | `dev.sh reset <module-id>`                                                                                                                  |
| `BROADCAST.md` notes aren't being seen by the Coder                               | use `dev.sh write` / `dev.sh iterate`, which inject them automatically                                                                 |
