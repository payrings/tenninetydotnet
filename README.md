# 10/90 .NET – Spec-Driven Autonomous Framework for .NET

<p align="center">
  <img src="docs/assets/architecture.svg" alt="10/90 tenninety architecture: preparation pipeline, frontier architect, guarded local execution loop, human supervision" width="920" />
</p>

**By G. Paganelli - rift-demote-fence@duck.com**

**10/90 (tenninety)** is a framework for software engineering teams building C# and .NET
applications with local coding agents. The name is the operating ideal ratio: roughly **10% of the
intelligence budget is spent on frontier reasoning** (planning, repair advice) and **90% on local
inference** (coding, review, testing), creating substantial savings in development cost.

## How it works

1. **Preparation** A business need is identified by the client, classic business analysis clarifies it, and a business analyst–developer pair runs [OpenSpec](https://openspec.dev/) to consolidate business and technical  requirements into one `spec.md`
   ([`docs/SPEC-AUTHORING.md`](docs/SPEC-AUTHORING.md)).
2. **Planning** A blueprint prompt casts a high-reasoning
   frontier model as the *principal architect*, decomposing the spec into atomic work packages
   (`plan.json`), strictly validated as a Directed Acyclic Graph (DAG).
3. **Execution** A local model acting as the **coder** (running inside
   [aider](https://aider.chat), [OpenCode](https://opencode.ai) or [Pi](https://pi.dev) – your
   choice) builds on its own git branch; another local model acting as the **reviewer** judges the work against the directives; a mechanical test suite gates every promotion to `main`.

Every gate is mechanically enforced rather than requested: framework-built prompt text is
sanitised, newly added secret-shaped files are kept out of commits, plans are re-validated after
every change, every promotion lands as ONE squashed commit on `main`, and history is never
rewritten. The framework enforces that the coder and reviewer identifiers differ; operators must
ensure those identifiers really resolve to different weights.

Humans stay in command through a real-time dashboard – pause, redirect (**pivot**: KEEP / REWORK /
CANCEL), or revert a bad promotion. The orchestrator is deliberately **waterfall**: once the plan
is accepted, jobs are built in strict dependency order with no machine-decided scope drift.
However, changes stay **agile**: new or altered requirements can be introduced at any stage – run them
through OpenSpec, then trigger a pivot so every existing package is classified KEEP / REWORK /
CANCEL. Completed work is normally classified KEEP.

**Platform: .NET 10 (`net10.0`) with C# 14.** Built on the latest .NET release; C# 14 features
(extension members, `field`-backed properties) are used where they clarify intent.

> [!WARNING]
> **This is an experimental alpha.** Live Docker mode runs Coder, Reviewer exploration, Restore,
> and Tester commands in hardened disposable containers. The authoritative repository is never
> mounted; only an exact disposable candidate is writable. The host still controls Docker and the
> local Reviewer model transport, so use a least-privilege Docker deployment, digest-pinned role
> images, and keep credentials out of project files. `sandbox.mode=unsafe-host` deliberately gives
> up this isolation and is never an automatic fallback.

## Quickstart (offline simulation – no models required)

All commands below are identical in **bash** and **fish** (fish 3+); no Windows shells are
supported or documented.

```bash
mkdir myproject && cd myproject
tenninety init                          # scaffolds .tenninety/, config, starter spec.md
$EDITOR spec.md                         # write your Business-Technical spec
tenninety plan --spec ./spec.md --yes   # Frontier decomposes → .tenninety/plan.json
tenninety start --headless              # autonomous serial execution of the whole queue
tenninety status                        # inspect queue & health
```

Out of the box `provider_mode` is `"mock"`: everything is simulated deterministically so the
whole pipeline runs offline. For live mode with real models, see
[`docs/INSTALL.md`](docs/INSTALL.md).

## Documentation

| Document | Audience |
| --- | --- |
| [`docs/OVERVIEW.md`](docs/OVERVIEW.md) | Everyone: how the framework works – execution model, distinct coder/reviewer requirement, aider, llama-swap, guarantees, repository layout |
| [`docs/INSTALL.md`](docs/INSTALL.md) | Installation on CachyOS: offline rehearsal, the default llama-swap container model service, and the isolated KVM path |
| [`docs/SPEC-AUTHORING.md`](docs/SPEC-AUTHORING.md) | Business analyst + developer: the recommended pipeline for producing `spec.md` (business need → analysis → OpenSpec → spec) |
| [`docs/JUNIOR-GUIDE.md`](docs/JUNIOR-GUIDE.md) | New to .NET/C#: every step explained, glossary, guided first run, exercises |
| [`docs/SENIOR-GUIDE.md`](docs/SENIOR-GUIDE.md) | Practitioners: command reference, state model, engine semantics, config, extension points, troubleshooting matrix |
| [`docs/TESTER-SANDBOX.md`](docs/TESTER-SANDBOX.md) | Operators: Docker role boundaries, restricted Restore acceptance, cleanup, recovery, and verification |
| [`docs/SANDBOX-CONFIG.example.jsonc`](docs/SANDBOX-CONFIG.example.jsonc) | Annotated live-Docker and restricted-Restore configuration template |

## License

Apache 2.0 – see [`LICENSE`](LICENSE).
