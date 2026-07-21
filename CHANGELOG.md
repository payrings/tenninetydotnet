# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/payrings/tenninetydotnet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/payrings/tenninetydotnet/releases/tag/v0.1.0
