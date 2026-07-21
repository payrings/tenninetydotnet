# 10/90 .NET – Hybrid Frontier/Local AI Development Framework

![readmeillustration](./readmeillustration.svg)

**By G. Paganelli**

An open architecture and framework for software engineering teams building C# and .NET systems with coding agents. It separates system design from mechanical execution: a high-reasoning cloud frontier model writes the specification once, two free local open-weight models handle routine implementation and automated peer review, and a deterministic test suite, with zero AI at runtime, acts as the quality gate. Roughly 10% of the intelligence budget goes to pay-per-token frontier reasoning, 90% to local inference that costs only electricity.

Every safety property of the pipeline is **mechanically enforced rather than requested**: read-only mounts, locked dependency manifests, hash-checked specifications, and signature-drift hooks operate below the agents' permission level, so a misbehaving model can fail a gate but cannot move it.

## Repository map

| Path | What it is |
|---|---|
| [`SETUP_GUIDE.md`](SETUP_GUIDE.md) | **One-time machine setup** (Phases 0–8): Linux host preparation, GPU runtime, local model serving, the two Docker sandbox images, agent profiles, frontier-model access, and host tooling. Ends with a setup verification checklist. Do this once per machine. |
| [`WORKING_GUIDE.md`](WORKING_GUIDE.md) | **Per-project operations** (Phases 9–13): scaffolding the solution, installing the specification, the module loop (write → review → test), escalation, and human review – plus the frontier blueprint prompt (Appendix A) and the contract-test pattern (Appendix B). Follow it once per project. |
| [`starter-kit/`](starter-kit/) | The pre-tested, executable template: the `dev.sh` orchestrator and all governance scripts, both Dockerfiles, build/package manifests, pre-commit hooks, and annotated specification templates. Both guides copy from here – nothing is written by hand. You can also click **Use this template** on GitHub to start from a compliant repository. |
| [`examples/`](examples/) | Sample configuration files referenced by the setup guide, such as the `llama-swap` serving configuration (`examples/config.yaml`, Phase 4). |
| [`docs/`](docs/) | Supplementary material – see below. |

### In `docs/`

| Document | What it is |
|---|---|
| [`developer-quickstart.md`](docs/developer-quickstart.md) | The short path for a developer who already holds a complete specification package for a small project: 21 steps pointing into the two guides by phase number, no content repeated. |
| [`1090-framework-explainer.md`](docs/1090-framework-explainer.md) | A technical explainer for engineers evaluating the framework – the problem, the four-layer architecture, the threat/control model, the operating loop, and outcomes. Read this to decide whether to adopt; read the guides to adopt. |
| [`scaling-to-medium-projects.md`](docs/scaling-to-medium-projects.md) | A **design document** (not installed behaviour) extending the framework to medium systems: specification sharding, an integration-real test tier, UI coverage, and throughput planning. Nothing in it is implemented in the starter kit yet. |

## Where to start

**Setting up a new machine?** Work through [`SETUP_GUIDE.md`](SETUP_GUIDE.md) top to bottom until its verification checklist passes. You never repeat it per project.

**Starting a project on a configured machine?** Go straight to [`WORKING_GUIDE.md`](WORKING_GUIDE.md), Phase 9. If you already have the full specification package in hand, [`docs/developer-quickstart.md`](docs/developer-quickstart.md) is the fastest route through both guides.

**Evaluating the framework before committing?** Read [`docs/1090-framework-explainer.md`](docs/1090-framework-explainer.md), and [`docs/scaling-to-medium-projects.md`](docs/scaling-to-medium-projects.md) if your target is larger than a small single-purpose tool.

Phase numbering is continuous across the two guides (setup owns 0–8, working owns 9–13), so any phase reference anywhere in this repository is unambiguous.

## Requirements at a glance

A discrete GPU with **24 GB of VRAM** (e.g. RX 7900 XTX, RTX 3090/4090) to run both local models at zero marginal cost; ~30 GB of disk; Docker, Git, Node 22+, Python 3.11+, and the .NET SDK; and an OpenRouter API key (or any OpenAI-compatible endpoint) for the one-time specification authoring and tiered escalation. The setup guide's step-by-step commands are written and tested for **Arch-based Linux (CachyOS) on an AMD RX 7900 XTX** and use Arch-specific tooling (`pacman`, `paru`, ROCm, `ufw`, Fish); the governance layer, sandbox topology, and deterministic gates are hardware- and language-agnostic, but other OSes or GPUs (macOS/Metal, Ubuntu/CUDA, Windows/WSL2) require translating those commands yourself. Smaller local models or cloud endpoints can substitute without changing a single rule of the workflow. Full details and compatibility notes are in the setup guide.

## License

Licensed under the Apache License, Version 2.0. See [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).

Copyright 2026 G. Paganelli

## Versioning and changes

The framework version is recorded in `DEV_SH_VERSION` (in `starter-kit/scripts/dev.sh`) and printed by `dev.sh version`. See [`CHANGELOG.md`](CHANGELOG.md) for release history. 
