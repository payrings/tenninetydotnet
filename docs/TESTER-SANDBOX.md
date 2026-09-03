# Docker role sandbox and Restore boundary

This document describes the implemented live Docker boundary for Coder, Reviewer exploration,
restricted Restore, and the mechanical Tester. The local Reviewer model call remains host-side,
but repository exploration occurs only through bounded commands in a fresh offline guest.

## Mode selection

`AgentFactory.CreateTester(IGitService authoritativeGit, Action<string>? log = null)` selects
the Tester implementation (fail closed):

| Configuration | Implementation |
|---|---|
| provider `mock` | `Mock/MockTesterAgent` — deterministic in-process pass/failure window. Never touches Docker, Git or a shell, even if the sandbox config enables restore or has empty images. |
| `sandbox.mode=docker` (live) | `SandboxCoderGate`, `SandboxReviewerGate`, and `SandboxTesterGate`. Docker resources are built lazily at run time and failures never fall back to host execution. |
| `sandbox.mode=unsafe-host` (explicit) | `Testing/UnsafeHostTesterAgent` — legacy host-shell compatibility. Emits a prominent WARNING through the log/audit path at construction and on every run. It is never a fallback for Docker failures, failed preflight, invalid images, failed workspace creation, timeouts, enabled restore or container startup failures — those fail closed. |
| anything else | error (fail closed). |

Live Docker selection validates the live configuration up front (digest-pinned images, coder
model endpoint). Merely selecting a mock Tester resolves no Docker executable, reads no
Docker settings and creates no temporary directories.

## Exact candidate identity

- The Tester receives a `TesterRunContext` (`Tenninety.Execution.Testing`) carrying ONLY:
  the trusted `CandidateRevision` (work branch, exact 40-hex commit SHA, recorded main base),
  the validated work-package id, the attempt number and a defensive advice snapshot. It
  cannot carry a repository path, workspace path, ingestion path, mounts, Docker arguments or
  a process launcher. Coder and Reviewer receive equivalent role-specific contexts with the
  exact `CandidateRevision`, not a host path.
- The engine passes the reviewed tip; the hotfix flow passes the exact post-revert hotfix SHA.
- `TestRunResult.CandidateSha` binds the result to the tested candidate. The engine and the
  hotfix flow reject a missing or mismatched identity — a pass without the exact requested
  candidate SHA is never accepted, and it is never "repaired" from the current HEAD.
- On the Docker path the identity comes from the materialized workspace revision (verified to
  equal the requested SHA), never from test output or the disposable `.git`.

## Disposable workspace ownership

- Every Docker Tester invocation materializes a fresh attempt workspace via the trusted
  `CandidateWorkspaceFactory` from the exact candidate commit (`source/` + `ingestion/`).
- The managed root comes from `sandbox.workspace_root` (validated, never modified or deleted)
  or, when unset, a fresh owner-only private directory beneath the system temp directory
  (never `/tmp` itself), which the Tester owns and deletes after the run.
- Only the validated disposable `source/` directory is mounted at `/workspace`. The
  authoritative repository, its `.git`, the ingestion directory, the attempt parent, the
  Docker socket, host caches, HOME and devices are never mounted.
- The container spec is a validated `SandboxSpec` (`SandboxRole.Tester`,
  `SandboxNetworkPolicy.None`, digest-pinned image, configured resource limits, complete
  non-secret management labels including `tenninety.candidate`).

## Offline build/test commands

- Build and test commands must both be non-blank. Build runs first; tests run only after a
  DEFINITIVE build success (`SandboxCommandResult.Succeeded`).
- Each command is a structured argument vector submitted through `ISandboxSession.RunAsync`:
  `/bin/bash --noprofile --norc -c <configured command>` at `/workspace`. The configured shell
  text is never executed on the host and never assembled into a `docker exec` string.
- Complete bounded capture: the typed Docker CLI adapter forwards the transport's captured
  output to the Tester UNCHANGED. The transport's combined stdout/stderr capture cap (the
  Tester submits a 1 MiB cap per command) is the ONLY bound on the decision input; there is
  NO intermediate presentation tail inside the adapter. Zero-test detection therefore sees
  the complete captured output (a "No tests found" summary early in large output can never
  be shortened away), presentation shortening happens only AFTER classification and
  sanitization, and the final user-facing report tail is bounded at 4,000 characters.
- `TENNINETY_WP` / `TENNINETY_ATTEMPT` are provided as structured environment values through
  the closed allowlist. Host environment variables are never copied. The `{wp}` template is
  substituted only for a validated bounded ASCII work-package identifier.
- Discovery (`TestProjectDiscovery`) runs on the materialized source before any container
  starts: bounded, deterministic, non-executing, DTD/external-entity prohibited, symlinks
  never followed, `.git`/`.tenninety`/`bin`/`obj`/`node_modules` skipped.
- Project files are opened through the trusted no-follow regular-file reader
  (`TrustedFileReader`): the opened object is proven to be a REGULAR file by descriptor
  `fstat` (FIFOs, sockets, devices, directories and symlinks are never opened as project
  files, so discovery cannot block on a FIFO), and the opened descriptor's identity, size and
  timestamps are re-verified after reading. A regular file EXACTLY at the byte limit still
  parses; an over-limit file is never examined. On a platform without the reader's
  regular-file proof discovery fails closed (no evidence of a test project). These are
  trusted-host checks, not a claim that same-user concurrent filesystem mutation is
  impossible.
- Special-file rejection in discovery is regression-tested through a BOUNDED harness: a
  future regression that opens (and blocks on) a FIFO/socket fails the single test after a
  fixed limit instead of hanging the whole suite.

## In-image dependencies and optional restricted Restore

- The Tester itself always runs fully offline: no network attachment or host caches. Dependencies
  must be pre-baked, vendored, or produced by the accepted Restore phase.
- When Restore is disabled (the default), a build/test command that needs network access fails.
- When Restore is enabled, Tenninety first captures a no-follow baseline, generates a trusted
  `NuGet.Config` containing only `approved_feeds`, and runs the fixed `dotnet restore --locked-mode`
  command in a separate `SandboxRole.Restore` container. The configured proxy environment is
  fixed by trusted code. Candidate NuGet configuration and arbitrary restore arguments are ignored.
- The Restore container is removed before post-Restore integrity validation. Only bounded derived
  regular files/directories may appear; source mutations, redirects, special files, excessive
  depth/count/size, quota overflow, or incomplete capture fail closed. A fresh `network=none`
  Tester then consumes the accepted tree.

## Restore operator acceptance

Restore is disabled by default. Enabling it requires a versioned
`tenninety.restore.v1` acceptance record that binds all of these operator-proven facts:

- The `repository` value printed as **Repository scope** by `tenninety status`, and instance
  `tenninety`.
- The exact pre-existing restricted Docker `network_name` and its 64-hex ID from
  `docker network inspect --format '{{.Id}}' <network_name>`.
- A non-loopback proxy URL and 1-64 exact HTTPS feed URLs. `feed_policy_sha256` is SHA-256 over
  `proxy=<proxy_url>\n` followed by canonical feed URLs sorted ordinally, one per line.
- A named firewall profile that actually limits the Restore network/proxy to the approved feeds.
  Tenninety records this claim; it does not configure or prove host firewall policy.
- A named hard storage quota, `hard_quota_enforced=true`, and `storage_quota_bytes` covering
  `max_derived_mb` (maximum 1 TiB). Tenninety validates the record and derived tree but does not
  configure the external quota.
- `accepted=true`, `operator_acknowledged=true`, and a future exact UTC round-trip
  `expires_utc`. Expired, mismatched, malformed, or stale records are refused before Restore.

See [`SANDBOX-CONFIG.example.jsonc`](SANDBOX-CONFIG.example.jsonc). Any feed, proxy, repository,
network, quota, or expiry change requires a new acceptance. Restore never silently becomes general
internet access and never falls back to host execution.

## Tester workspace deletion behavior

- Build artifacts, test mutations and the disposable `.git` are discarded with the attempt.
- The Tester path always deletes the attempt workspace — including FAILED runs.
  `sandbox.keep_failed_workspaces` is deliberately NOT honored here; a workspace that cannot
  be cleaned up safely is reported as an error, never as a retention mode.
- Deletion revalidates the managed-root directory chain (the root itself and every ancestor)
  immediately before the destructive step and rejects redirects: a root or ancestor replaced
  by a symlink refuses the deletion and everything behind the redirect is preserved. It
  refuses to follow a symlink into or out of the tree, never deletes the managed root or
  another attempt, and verifies absence afterwards. It runs outside caller cancellation using
  bounded cleanup.
- Presence and TYPE of the deletion target are checked NO-FOLLOW (Linux `lstat`; a
  conservative managed fallback on other hosts): genuine absence is distinguished from an
  existing regular file, special file (FIFO/socket/device) or redirect — a non-directory
  entry is NEVER treated as "already absent". An inspection failure never counts as absence
  (it fails closed), and an unexpected entry type is PRESERVED and reported as an
  infrastructure/retention failure instead of being deleted merely to make cleanup pass.
  Absence is positively re-verified after the deletion. An inspection failure at the
  `lstat` level (e.g. ENOTDIR beneath a non-directory ancestor) is regression-tested
  DIRECTLY against the inspection primitive. On non-Linux hosts the no-follow
  check falls back to conservative managed semantics that never treat a provably existing
  entry as absent (their remaining blind spot for exotic special entries is documented in
  the source).
- A FAILED attempt-workspace deletion marks the workspace RETAINED: it is never followed by a
  broader recursive deletion of its parent, and an automatically owned managed root is
  removed only when it is proven safe and EMPTY (non-recursive deletion; unexpected contents
  are retained and reported instead of deleted). Genuine absence of the owned root is a clean
  no-op; an owned-root path that is not a real directory is refused and preserved. A
  configured managed root is never deleted.

## Preflight warnings

A ready preflight (`IsReady == true`) can still carry reduced-protection warnings (for
example a daemon that reports no SELinux). The gate surfaces these warnings — bounded and
sanitized — through the supplied log/audit callback BEFORE any candidate code executes.
Warnings never silently disappear behind a ready preflight and are never upgraded into claims
of protection. Hard preflight errors keep blocking the run.

## Failure classification: ordinary vs infrastructure (control flow, not message text)

The gate distinguishes failures by CONTROL FLOW, using a typed
`TesterInfrastructureException` — never by message text or the `(tester-gate)` label:

- **Ordinary candidate build/test failures** (definitive nonzero build/test exits, explicit
  zero-test outcomes, and operational indeterminacies such as timeout, OOM, output
  truncation and exhausted command budgets) are returned as regular failed `TestRunResult`
  values and keep the normal Coder retry/escalation path. They can never pass (fail closed).
  A definitive nonzero exit (exit code >= 1) is always an ordinary candidate failure, even
  after a repair of the synthetic-exit classification.
- **Synthetic negative exits are infrastructure failures**: a negative exit code WITHOUT any
  operational flag is the typed signature of a command whose process never produced a
  definitive exit (a transport startup or I/O failure). The Tester boundary classifies it as
  an indeterminate `TesterInfrastructureException` — never an ordinary candidate failure, so
  the engine performs no automatic Coder retry, no Frontier escalation and no promotion for
  it. Flagged outcomes (timeout, cancellation, OOM, truncation) keep their own documented
  classification.
- **Infrastructure/refusal failures** (invalid Restore acceptance or integrity evidence,
  failed preflight, failed materialization, failed container creation, session
  infrastructure exceptions, the synthetic negative exit above, an infrastructure-layer
  command cancellation without caller cancellation, authoritative host-state mismatch, and
  unproven cleanup/retention) THROW
  `TesterInfrastructureException`. The engine's existing infrastructure-exception path then
  aborts the run: no automatic coding retry, no promotion, no Frontier escalation, resumable
  state.

## Cancellation precedence

- The caller's token is checked before any resource acquisition and before submitting each
  subsequent command; an already-cancelled caller starts no Docker or workspace work.
- A lower layer may TERMINATE the container and RETURN a `Cancelled=true` result instead of
  throwing. When the caller's token actually fired, the gate propagates the cancellation
  AFTER proven cleanup — as a SAFE, controlled caller-cancellation exception. A raw
  underlying cancellation exception (arbitrary message text or inner exception chain) is
  never rethrown.
- A `Cancelled=true` result WITHOUT caller cancellation is classified
  as an indeterminate INFRASTRUCTURE failure — never a pass and never attributed to the user.
- Cleanup runs independently of the cancelled caller token. If cancellation and a cleanup
  failure coincide, both facts surface (cancellation + retained-resource evidence); retained
  resources are never concealed behind a cancellation exception.

## Controlled diagnostics

- Every public Tester exception, log line, audit entry and candidate feedback string is
  constructed from CONTROLLED failure categories/stages and bounded non-secret identifiers
  (validated run labels, container IDs, generated directory basenames, and commit-SHA-shaped
  identifiers). Underlying exception messages, inner exception chains, daemon output, raw
  invalid configuration values, raw branch strings and raw git output are NEVER copied into
  them: the sanitizer is defense in depth, not proof that arbitrary text is safe to publish.
- Message provenance is established by the explicit `TesterInfrastructureException.Provenance`
  marker — never by the exception's CLR type alone (any lower layer or injected session can
  throw the same type with arbitrary text through its public constructor) and never by
  string matching. Only Tester-controlled instances are published verbatim; everything else,
  including an untrusted instance of the same type, is reduced to the controlled stage label
  plus the exception type name. No arbitrary inner exception chain is attached to public
  failures.
- Early validation (context and structural configuration) and the initial/final host-state
  inspections run INSIDE the boundary: validation failures and git inspection failures are
  reduced to controlled categories. Host-state mismatches are described by category — branch
  strings are never echoed, and only commit-SHA-shaped, length-bounded identifiers appear.
  The checks themselves are unchanged and still fail closed before resource acquisition.
- Preflight errors and warnings are controlled compositions: fixed stage/category text,
  validated configuration identifiers and exception type names. Cgroup enforcement errors
  represent recognized cgroup versions/drivers by fixed allowlisted labels ("cgroup v1/v2",
  "systemd", "cgroupfs"); unknown, missing or malformed daemon values are described by
  category with the original text withheld — parsed daemon facts are arbitrary strings and
  are never automatically safe for public presentation. Probe-inspection mismatches likewise
  describe the deviation by category and withhold the daemon-provided value.
- Exactly ONE final length bound (`SandboxTesterGate.MaxPublicTesterMessageChars`, 4000
  characters) is applied to the COMPLETE public message after every prefix, category,
  truncation marker and retention detail is assembled. The result NEVER exceeds 4000
  characters: when the assembled text is longer, the middle is elided under a fixed
  "…[bounded]" marker and the marker's space is reserved first, so both the head (primary
  failure categories) and the tail (retained-resource evidence) survive truncation. Warning
  log lines are bounded the same way AFTER their prefix is added — this contract covers the
  string the Tester hands to the logger; an external sink's own formatting is not claimed.
- Ordinary captured build/test output keeps its existing path: sanitized BEFORE the
  presentation tail is selected, with the final 4,000-character bound applied after
  operational reasons, zero-test explanations and build-failure suffixes. Compiler and test
  diagnostics are NOT replaced with generic infrastructure messages on that path.

## Failure and cleanup reporting

- If container removal cannot be proven (e.g. session disposal fails or container creation
  throws), the workspace is conservatively RETAINED and the run fails with bounded
  diagnostics — never a pass, and the workspace is never deleted while a container may still
  be writing it.
- Primary failures and cleanup failures are both preserved in bounded, controlled
  diagnostics (see "Controlled diagnostics"); retained-resource reports carry non-secret
  identifiers only (run label, container id when known, owned directory basenames), and no
  arbitrary exception chain is attached.
- Timeout, cancellation, OOM, output truncation and zero-test summaries can never pass.
  Zero-test detection uses the complete bounded output (including the "No tests found" and
  "No tests executed" forms), not only the report tail; it applies to the TEST command only —
  a successful build never fails because its output merely contains a zero-test-looking
  phrase.
- The captured output is sanitized BEFORE the presentation tail is selected (so bounding can
  never strip a secret's identifying prefix while keeping its value), and the final
  presentation bound is applied after operational reasons, zero-test explanations and
  build-failure suffixes.
- After the run, the authoritative branch/HEAD/main/clean state is rechecked; any change
  fails the gate without repair.

## Running the focused tests

```bash
dotnet build tenninety.slnx -c Release --no-restore
env -u TENNINETY_RUN_DOCKER_TESTS -u TENNINETY_TEST_IMAGE dotnet test \
  tests/Tenninety.Tests/Tenninety.Tests.csproj -c Release --no-build --no-restore \
  --filter "FullyQualifiedName~TesterContextTests|FullyQualifiedName~TestProjectDiscoveryTests|FullyQualifiedName~TestOutputClassifierTests|FullyQualifiedName~ShellTesterAgentTests|FullyQualifiedName~SandboxTesterGateTests"
```

Docker opt-in integration tests are skipped unless `TENNINETY_RUN_DOCKER_TESTS=1` and an
exact `TENNINETY_TEST_IMAGE` (sha256:<64 hex> local image ID) are provided. Images are never
pulled or built by tests.

### Role and end-to-end Docker categories

Five additional categories are DISCOVERED always and reported skipped with a precise
prerequisite message until their own opt-in is set:

| Category trait | Opt-in | Prerequisites |
|---|---|---|
| `Category=DockerCoder` | `TENNINETY_RUN_DOCKER_CODER_TESTS=1` | `TENNINETY_CODER_TEST_IMAGE`, `TENNINETY_REVIEWER_TEST_IMAGE`, `TENNINETY_TESTER_TEST_IMAGE` (exact local sha256 IDs, numeric non-root USER, no ENTRYPOINT), `TENNINETY_TEST_MODEL_NETWORK` (pre-existing), `TENNINETY_CODER_TEST_MODEL_ENDPOINT` |
| `Category=DockerReviewer` | `TENNINETY_RUN_DOCKER_REVIEWER_TESTS=1` | same role images + model network + endpoint |
| `Category=DockerTester` | `TENNINETY_RUN_DOCKER_TESTER_TESTS=1` | same role images + model network + endpoint; tester image must contain the .NET SDK |
| `Category=DockerRestore` | `TENNINETY_RUN_DOCKER_RESTORE_TESTS=1` | same role images + the complete operator contract: `TENNINETY_RESTORE_TEST_NETWORK`, `TENNINETY_RESTORE_TEST_NETWORK_ID`, `TENNINETY_RESTORE_TEST_PROXY_URL`, `TENNINETY_RESTORE_TEST_FEEDS`, `TENNINETY_RESTORE_TEST_QUOTA_BYTES`, `TENNINETY_RESTORE_TEST_QUOTA_ID`, `TENNINETY_RESTORE_TEST_FIREWALL_PROFILE`, `TENNINETY_RESTORE_TEST_EXPIRES_UTC`, `TENNINETY_RESTORE_TEST_OPERATOR_ACK=1` |
| `Category=DockerEndToEnd` | `TENNINETY_RUN_DOCKER_E2E_TESTS=1` | same role images + model network + endpoint |

Once opted in, a missing or malformed image, endpoint, network or Restore acceptance FAILS
the test through pure validation BEFORE any Docker or network use — a requested run is never
converted into a skip. Positive runs require every prerequisite to already exist locally;
nothing is pulled, built or substituted. The Coder gate uses a deterministic guest fixture
command (real Aider/OpenCode/Pi behavior is gated separately by
`TENNINETY_RUN_DOCKER_CODER_REAL_TOOL_TESTS=1`), the Reviewer gate uses a deterministic
scripted host-side model client, and the end-to-end test drives Coder → trusted promotion →
fresh Reviewer → fresh Tester with exact candidate SHA propagation.

## Startup recovery and remaining limitations

- Before a Docker role creates a container, its exact attempt root and complete management labels
  are atomically recorded in `.tenninety/sandbox-resources.json`; the exact returned container ID
  is added immediately. Records clear only after positively proven container and workspace cleanup.
- On every start, while holding the repository daemon lock, recovery lists only containers bearing
  this instance and collision-resistant repository identity. It also retries exact journaled IDs.
  It removes workspaces only when the journal proves they are direct `attempt-*` children of the
  recorded non-overlapping managed root. Unrelated containers, directories, and siblings are not
  discovered by prefix/timestamp scans and are never deleted.
- Any malformed journal, failed inventory, failed removal, unexpected path type, or unresolved
  workspace is persisted as `SandboxRecoveryInfo` quarantine and scheduling is refused. Recovery
  retries on the next start; `tenninety status` reports its facts.
- Positive live-Docker execution HAS been exercised in the stable-release gate: against a
  real Docker 29.7.2 daemon with a local image (explicit numeric non-root USER, no
  ENTRYPOINT, .NET SDK for the Tester) and the pre-existing `tenninety-coder-model` network,
  the deterministic DockerCoder, scripted DockerReviewer, offline DockerTester (offline
  build/test plus implicit-restore rejection) and deterministic DockerEndToEnd categories, and
  the generic Docker transport/runtime/session/preflight category, all pass with hardening
  inspection, quiescence/removal/absence proofs and workspace cleanup. Real Aider/OpenCode/Pi
  behavior stays separately opted in (`TENNINETY_RUN_DOCKER_CODER_REAL_TOOL_TESTS=1`) and is
  not required for those gates. `Category=DockerRestore` remains default-disabled and its
  positive live gate is NOT validated: no real operator contract (restricted network id,
  proxy, feeds, quota, firewall profile, expiry, acknowledgement) exists in this environment.
- Cleanup failure handling under real daemon failure modes (busy daemon, zombie containers)
  is unit-tested through fakes only.
- The path revalidation performed before destructive cleanup closes redirect and containment
  gaps for the trusted host's own decisions, but it cannot make arbitrary same-user
  concurrent filesystem mutation impossible; an attacker running as the same user retains
  the usual POSIX abilities.
- The no-follow presence/type check uses Linux `lstat` directly; on non-Linux hosts the
  conservative managed fallback cannot identify every exotic special entry (its blind spot
  is documented in `TrustedWorkspaceDeletion`). The Tester deletion path itself is a Linux
  host path in this phase.
- The `DockerCli` adapter still attaches a bounded (512-character) daemon-stderr excerpt to
  its own operation-failure exceptions for internal diagnostics; Tester public diagnostics
  never copy that text (they reduce exceptions to controlled categories), but the excerpt
  itself is daemon-controlled text and is not proven free of host paths.
- Pre-journal leaked `tenninety-*-root-*` directories are intentionally not inferred or deleted;
  an operator must inspect those legacy resources explicitly.
