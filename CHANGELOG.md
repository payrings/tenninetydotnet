# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Framework regression CI now exercises host runtime isolation, queue state
  transitions, disposable snapshot behavior, staged raw-SQL inspection,
  rename-aware scope, ignored build controls, the SDK pin, and semantic alias
  drift; third-party actions are pinned to immutable commit IDs.
- `dev.sh runtime-path` reports the external per-workspace state location.
- A dedicated staged-content raw-SQL checker replaces the working-tree grep.

### Changed
- Gate markers, locks, test logs, reset backups, repair baselines, escalation
  counters, and frontier output now live outside the agent-visible workspace.
- Restore runs from a Git-derived disposable seed; every test project builds and
  executes in its own clone with no network and the NuGet cache read-only.
- Public API drift now compares complete resolved Roslyn/MSBuild compilations
  for every source target framework instead of syntax from changed files only,
  including explicit type accessibility, modifiers, and enum underlyings. It
  runs in a dedicated compiled host so SDK registration precedes MSBuild loads.
- Escalation payloads are bounded, calls are serialized, project-local `.env`
  files are ignored, and incomplete provider responses do not consume a tier.

### Fixed
- Ignored and implicit MSBuild controls (`*.user`, `*.rsp`, `.editorconfig`, and
  case-insensitive NuGet configuration) can no longer bypass scope or test gates.
- Approved or already queued modules cannot be reopened through `queue`.
- Scope/reset inspect both sides of renames and copies, and scope rejects empty
  or metadata/protected-test-only module implementations.
- Queue, approval, rejection, interface propagation, and reset backup paths now
  fail closed and restore or preserve their prior state on partial failure.
- Content fingerprints, architecture-change detection, Git status checks, and
  mutation locking now fail closed on plumbing/tool errors; gate writes are
  atomic.
- Module identifiers are validated before any path construction; agent context,
  build-control mount discovery, queue counters, prompt staging, and manifest
  coverage also fail closed instead of accepting incomplete enumeration.
- Build-control mounts are deduplicated, rejection rollback restores both queue
  and feedback state, and corrupt external baseline markers fall back to
  durable Git tags or review commits.
- The semantic signature checker no longer runs under a script host that
  preloads `Microsoft.Build.Framework`; its compiled host excludes NuGet
  MSBuild runtime assets, verifies a clean output, registers the selected SDK
  first, uses Roslyn's current workspace-failure API, and preserves nullable
  flow analysis for fatal exits. Its audited XML-cryptography dependency is
  pinned to the serviced `10.0.10` release, and CI preserves restore failures.
- Python bytecode is ignored at the repository root, and the regression
  workflow uses the Node 24-based `actions/checkout` release.
- Dependency propagation rejects duplicate and unknown Module IDs before making
  one atomic review-queue update.
- Contract-test batches and the canonical golden harness roll back or remain
  absent on installation failure; per-project sandboxes use collision-free
  destinations.
- Repair review instructions now distinguish a baseline diff from the attached
  complete module, avoiding false "incomplete" findings for unchanged files.
- `global.json` now pins the complete `10.0.100` SDK version used by the test
  image.
- `show-frontier-fix` now prints the generated fix content.

## [0.2.1] - 2026-08-05

Hardening release for module baselines, protected artefacts, test isolation,
interface drift, escalation tiers, and reset safety. `DEV_SH_VERSION` in
`starter-kit/scripts/dev.sh` is `0.2.1`.

### Fixed
- Contract-test batches now match the manifest's exact protected `.cs` paths;
  protected host-generated tests pass module scope while agents still receive
  every Contracts, Golden, fixture, architecture, and build-control input
  read-only.
- Restore/build/test invocations now use workspace integrity hashes, mask
  `.env`, keep the offline NuGet cache read-only, and reject empty required test
  tiers. Build-control inputs are agent-immutable because MSBuild may evaluate
  them during networked restore.
- Approval and rejection metadata are committed immediately. Rejection creates
  a fresh repair baseline, and review, tests, drift checks, and escalation use
  the active initial/repair baseline instead of an obsolete start tag.
- Interface drift fails closed, covers operators, conversions, enum members and
  primary constructors, and marks transitive consumers by Module ID using the
  manifest dependency graph.
- Escalation enforces exact plan/override/write-code tiers, writes counters
  atomically, and refuses a fourth call.
- `reset` backs up and discards only uncommitted module work; it never moves
  `HEAD` or removes unrelated later commits.
- Restricted agent networking fails closed, and the setup guide now separates
  host-bound `ufw` policy from forwarded Docker egress policy.
- The golden harness correctly unwraps `Task<T>`, resolves entry points only in
  the production assembly, rejects extra input keys, and canonicalises
  equivalent JSON number spellings.

## [0.2.0] - 2026-08-03

Agent runtime migrated from the Cline CLI to aider. `DEV_SH_VERSION` in
`starter-kit/scripts/dev.sh` is `0.2.0`; tag this commit `v0.2.0` so the
constant, the tag, and this entry agree.

### Changed
- **Agent runtime:** the Cline CLI (Node.js) is replaced by aider
  (`aider-chat` 0.86.2, Python). `starter-kit/Dockerfile.cline` is removed and
  replaced by `starter-kit/Dockerfile.aider`; the agent image is renamed
  `cline-sandboxed` → `aider-sandboxed`.
- **Spec/skills directory renamed** `.cline/` → `.agent/`; every script, hook,
  template, and guide now reads from `.agent/rules/architecture.md` and
  `.agent/skills/`.
- **`dev.sh` agent invocations** are now single-shot `aider --message-file`
  calls with fully explicit context: the spec and skill files are attached
  (`--read`), the module's manifest files are attached editable to the Coder,
  and the module diff is generated on the host and inlined into the Reviewer's
  prompt (aider is single-shot, not agentic – nothing is auto-loaded).
- **`dev.sh write` now requires a Module ID**: `dev.sh write <module-id>
  "<task>"`, so the manifest's files can be attached to the Coder call.
- **`write-contract` staging** now uses an out-of-workspace `/staging` working
  directory instead of the in-workspace `.cline-output` directory.
- **Machine profiles** are now plain YAML/JSON at `~/.aider-coder` and
  `~/.aider-reviewer` (installed from `starter-kit/aider-conf/`), mounted
  read-only at `/conf`; the interactive `cline auth` step is gone.
- **Host requirements:** Node.js is no longer required on the host (aider runs
  inside its container).

### Added
- `starter-kit/Dockerfile.aider` (Python 3.12 + `aider-chat` 0.86.2, non-root
  UID/GID mapping, `aider-sandboxed` image).
- `starter-kit/aider-conf/coder/` and `starter-kit/aider-conf/reviewer/`
  profile templates (`aider.conf.yml`, `model-settings.yml`,
  `model-metadata.json`), installed to `~/.aider-coder` / `~/.aider-reviewer`
  in SETUP_GUIDE Phase 7 and mounted read-only at `/conf`.
- Explicit per-call context assembly in `dev.sh`: spec + skills attached, the
  target module's manifest files attached editable to the Coder, and the
  host-generated module diff inlined into the Reviewer prompt.

### Removed
- `starter-kit/Dockerfile.cline` and the Cline CLI runtime (and with it the
  host Node.js requirement and the interactive `cline auth` profile setup).
- `starter-kit/.cline/` (renamed to `starter-kit/.agent/`).

## [0.1.0] - 2026-07-21

Initial public release. `DEV_SH_VERSION` in `starter-kit/scripts/dev.sh` is
`0.1.0`; tag this commit `v0.1.0` so the constant, the tag, and this entry agree.

### Added
- `dev.sh` orchestrator: module lifecycle (`start` → `iterate` → `finalise` →
  `commit` → `queue`), sole owner of all Git state.
- Deterministic, orchestrator-level scope gate (`scope_check`) that parses each
  module's manifest from `.cline/rules/architecture.md` and hard-fails
  out-of-scope edits *before* the Reviewer model runs.
- Interface-change (spec-drift) human gate: `finalise`/`commit` refuse a diff
  that touches `architecture.md` without `--allow-spec-change`, printing the
  change against the frozen `architecture.original.md`.
- Fail-closed verdict parsing and a llama-swap preflight check.
- Two-phase test execution: networked `dotnet restore --locked-mode`, then
  `dotnet build`/`dotnet test` under `--network=none`.
- Canonical, pre-tested golden harness instantiated by
  `dev.sh write-golden-harness` (not agent-authored); write-once contract-test
  staging via `dev.sh write-contract`.
- Mechanical `check-coverage`, per-workspace `flock`, CWD guard, and recoverable
  `reset` backups.
- Apache-2.0 `LICENSE` and `NOTICE`.
- CI (ShellCheck, `bash -n`, markdown link check, Python compile, and a hermetic
  scaffold smoke test) and this changelog.

[Unreleased]: https://github.com/payrings/tenninetydotnet/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/payrings/tenninetydotnet/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/payrings/tenninetydotnet/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/payrings/tenninetydotnet/releases/tag/v0.1.0
