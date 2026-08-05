# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.1] - 2026-08-04

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
