# Installing 10/90 (tenninety) on CachyOS

This guide covers the first steps with the framework: a quick offline rehearsal, then the
default live model service — one llama-swap container serving coder and reviewer from a single
AMD Radeon RX 7900 XTX — which pairs with Tenninety's Docker sandbox for isolated live
execution on the host.

For the strongest isolation (tenninety and its Docker daemon confined to a disposable KVM
guest), continue with [INSTALL-KVM.md](INSTALL-KVM.md) after completing outcomes A and B.

Target physical machine:

- CachyOS or another current Arch-based system.
- Fish shell for interactive commands.
- 32 GB RAM and one 24 GB VRAM GPU.
- An Intel or AMD processor with hardware virtualization (Outcome C only).
- Internet access for installation, model downloads, NuGet, GitHub, and the Frontier provider.

Simple command blocks work in both fish and bash. Blocks explicitly labelled `bash` use bash
syntax. Fish environment-variable syntax is shown as `set -gx NAME value`; the bash equivalent is
`export NAME=value`.

---

## Read this first

### The outcomes

| Outcome | Sections | Result |
| --- | --- | --- |
| A. Offline rehearsal | 1-2 | The framework builds, its tests pass, and a mock project runs without models or keys. |
| B. Container model service (default live path) | 3-5 | One llama-swap container serves two GGUF models from a single AMD GPU; live execution uses the Docker sandbox. |
| C. Isolated live execution | [INSTALL-KVM.md](INSTALL-KVM.md) | Agents run as a non-sudo user in a disposable KVM guest with no host mounts or device passthrough. |
| D. Direct-host live mode (not isolated) | [INSTALL-KVM.md](INSTALL-KVM.md), section 15 | Clearly non-isolated fallback; never a default. |

Outcome B is the repository's default live deployment. Outcome C adds a second hardware-backed
boundary around the trusted orchestrator, Docker control plane and model tunnel, and is the
recommended configuration when agents work on anything sensitive. Outcome D exists only as an
explicitly non-isolated fallback.

---

## 0. Requirements

| Requirement | Needed for | Installed where |
| --- | --- | --- |
| .NET 10 SDK and ASP.NET targeting pack | framework and generated project | outcome A/B host |
| Git 2.40 or newer | all framework state and promotion | same OS as tenninety |
| Docker with the Compose plugin | outcome B model server | outcome B host |
| Working AMD Mesa Vulkan stack (`mesa`, `vulkan-radeon`) with `/dev/dri` | outcome B GPU inference | outcome B host |
| Two genuinely different GGUF models | coder and independent reviewer | outcome B host (`models/`) |
| Frontier-compatible HTTPS API and key | live planning and repair advice | key entered only in the live session |

Outcome C has additional requirements — KVM/QEMU, libvirt, virt-manager, host llama-swap, and
about 90 GB of disk — documented in [INSTALL-KVM.md](INSTALL-KVM.md). Mock mode needs only
.NET, Git, the framework source, and about 200 MB of build space.

---

## 1. Install and test the framework on the physical host

This section is the easy rehearsal path. It is not the isolated live setup.

### 1.1 Install core tools

```fish
sudo pacman -Syu --needed \
    git dotnet-sdk aspnet-runtime aspnet-targeting-pack \
    curl jq openssh openssl rsync
```

Verify:

```fish
dotnet --version     # expect .NET 10, for example 10.0.111
git --version
```

Set the identity the framework uses for its Git commits:

```fish
git config --global user.name "Your Name"
git config --global user.email "you@example.com"
```

Optional telemetry setting:

```fish
set -gx DOTNET_CLI_TELEMETRY_OPTOUT 1
```

If the CachyOS repositories do not provide .NET 10, use Microsoft's official installation
instructions rather than mixing unknown third-party packages. The repository's `global.json`
allows a compatible later .NET 10 feature band.

### 1.2 Clone, build, and test

```fish
cd ~
git clone https://github.com/payrings/tenninetydotnet.git 10-90new
cd ~/10-90new

dotnet build -c Release
dotnet test -c Release
```

The current validated baseline is 1,000+ passing tests, zero failures, and a Release build with
zero warnings and zero errors (the Docker integration categories are discovered but skipped
until their documented opt-in environment variables are provided; see
`docs/TESTER-SANDBOX.md`).

The executable is created at:

```text
~/10-90new/src/Tenninety.Cli/bin/Release/net10.0/tenninety
```

### 1.3 Put `tenninety` on the physical-host PATH

```fish
mkdir -p ~/.local/bin
ln -sf \
    ~/10-90new/src/Tenninety.Cli/bin/Release/net10.0/tenninety \
    ~/.local/bin/tenninety
```

Recent fish versions detect `$HOME/.local/bin`. Start a fresh terminal and verify:

```fish
type -q tenninety
tenninety --help
```

If `type -q` fails, add the directory once and restart fish:

```fish
fish_add_path --universal ~/.local/bin
exec fish
```

---

## 2. Run an offline mock project

Mock mode proves the queue, branches, reviews, tests, promotion, and state handling without giving
an agent access to a real model.

```fish
mkdir ~/tenninety-mock
cd ~/tenninety-mock

tenninety init
$EDITOR spec.md
tenninety plan --spec ./spec.md --yes
tenninety start --headless
tenninety status
```

What happens:

1. `init` creates a Git repository on `main`, `.tenninety/config.json`, and a starter `spec.md`.
2. The default `provider_mode` is `mock`.
3. `plan` creates and validates `.tenninety/plan.json`.
4. `start` executes each work package serially and promotes successful work to `main`.

Mock output is deterministic framework rehearsal material, not model-written application code.

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | completed, paused, or deliberately stopped |
| `1` | runtime error |
| `2` | command usage error |
| `4` | queue deadlock because BLOCKED work prevents dependent work |

---

## 3. Prepare your specification

The framework consumes one specification file, `spec.md`. Author it on the physical host (or
anywhere convenient), for example under `~/specs/<project>/spec.md`. Do not build the project
directly in a physical-host folder when you plan to use the isolated KVM path: only the
`spec.md` file is transferred into the guest, after the secure guest is ready
([INSTALL-KVM.md](INSTALL-KVM.md), section 11).

---

## 4. Decide whether to continue to live mode

Stop after Section 2 if you only need the mock rehearsal.

Continue when all of these statements are true:

- The Release build and tests pass.
- Hardware GPU inference already works or you are prepared to configure it.
- You have two different model weights, not two aliases for the same weights.
- You have a Frontier endpoint and a dedicated, spending-limited API key.
- You accept that the candidate workspace is disposable and may be destroyed by an agent.
- You will not mount any physical-host folder into the VM.

---

For Outcome B, live execution then follows the Docker sandbox configuration documented in
[`SANDBOX-CONFIG.example.jsonc`](SANDBOX-CONFIG.example.jsonc) and
[`TESTER-SANDBOX.md`](TESTER-SANDBOX.md).

---

## 5. Model serving topology (AMD Radeon RX 7900 XTX / Vulkan)

Model serving never uses vLLM and never needs a second GPU. One physical **AMD Radeon
RX 7900 XTX** serves both models (coder and reviewer) through **llama-swap**, which swaps two
llama.cpp Vulkan profiles in and out of the single card on demand – only one GGUF is resident
in VRAM at a time.

Prerequisites for the default container path on CachyOS/Arch: Docker with the Compose plugin,
a working AMD Mesa Vulkan stack (`mesa`, `vulkan-radeon`; verify with `vulkaninfo`), and the
GPU's DRM render nodes under `/dev/dri`. The llama.cpp build itself comes inside the container,
so nothing GPU-related needs to be installed on the host beyond the drivers.

**Default (after Sections 1-2; Docker sandbox on the host): the repository's llama-swap container.**
`docker compose up -d` starts one llama-swap container (pinned upstream `unified-vulkan` image
with bundled llama.cpp) with `docker/llama-swap.yaml` as its profile file, publishes the model
API on host loopback only (`127.0.0.1:8080`), and creates the internal
`tenninety-coder-model` network. Supply the weights as `models/coder.gguf` and
`models/reviewer.gguf` (or set `TENNINETY_MODELS_DIR` in `.env`); see `models/README.md`.
Verify with `curl http://127.0.0.1:8080/v1/models` – it must list both identifiers.
The sandboxed Coder reaches the same container by its Docker DNS name through
`"llama_swap_coder_endpoint": "http://llama-swap:8080/v1"` (that name resolves only inside
Docker networking; host loopback is rejected in the container context), while host-side
processes use `"llama_swap_endpoint": "http://127.0.0.1:8080/v1"`. If you follow the host-llama-swap path in
[INSTALL-KVM.md](INSTALL-KVM.md) (sections 2-3) instead and run llama-swap directly on the
host, stop that user service first – both bind `127.0.0.1:8080`.

**Alternative (Outcome C, tenninety inside the KVM guest): host-side llama-swap.**
[INSTALL-KVM.md](INSTALL-KVM.md) sections 2-3 install llama-swap on the physical host with
`listen: 127.0.0.1:8080` and section 9 reaches the guest through the SSH reverse tunnel
(`127.0.0.1:18080` inside the guest); the guest keeps `docker compose up -d` only to provision
its internal model network. The container
endpoints above do not apply to that path – inside the guest, `sandbox.mode=unsafe-host` is
the documented configuration precisely because the guest-loopback tunnel endpoint cannot pass
the Docker sandbox's container-endpoint validation.

In both topologies the model identifiers (`local_models.coder` / `local_models.reviewer`)
must match the llama-swap profile names, and the framework only enforces that the two
identifiers differ – you remain responsible for ensuring they resolve to genuinely different
weights.

---

Security notes for this path: the model API is published on host loopback only; the llama-swap
container is the only non-role member of the internal `tenninety-coder-model` network; the
disposable Coder receives no general egress through it. See [`OVERVIEW.md`](OVERVIEW.md)
(security) and [`TESTER-SANDBOX.md`](TESTER-SANDBOX.md) for the Docker boundary.

---

## 6. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Model identifier startup abort | coder and reviewer names match | use distinct aliases and verify they point to different GGUF checksums |
| Aider repeatedly exits | aider missing, model name wrong, or endpoint unavailable | run `aider --version`; query `/v1/models`; inspect llama-swap container logs (`docker compose logs llama-swap`) |
| Frontier planning fails | endpoint/key/network incorrect | verify public HTTPS, provider URL, model ID, and session key |
| `Working tree is not clean` | uncommitted config or unrelated edits | inspect `git status`; commit only intended configuration before starting |
| Queue deadlocks with exit 4 | BLOCKED work package gates dependents | fix root cause or pivot REWORK, then resume and start |
| Startup error: coder endpoint `refers to the container itself` | container-side model endpoint uses host loopback | set `llama_swap_coder_endpoint` to the Docker-network address (e.g. `http://llama-swap:8080/v1`), never `127.0.0.1`/`localhost` |

---

Useful diagnostics:

```fish
# Model server (outcome B)
docker compose ps
docker compose logs llama-swap | tail -50
curl --fail --silent http://127.0.0.1:8080/v1/models | jq .
ss -ltn '( sport = :8080 )'    # must list 127.0.0.1:8080 only

# Framework
tenninety status
```

---

## 7. References

- [10/90 overview](OVERVIEW.md)
- [Specification authoring](SPEC-AUTHORING.md)
- [Junior guide](JUNIOR-GUIDE.md)
- [Senior guide](SENIOR-GUIDE.md)
- [Tester sandbox](TESTER-SANDBOX.md)
- [Sandbox configuration template](SANDBOX-CONFIG.example.jsonc)
- [Model placement](../models/README.md)
- [llama-swap repository](https://github.com/mostlygeek/llama-swap)
- [llama-swap configuration overview](https://github.com/mostlygeek/llama-swap/blob/main/docs/kb/guides/configuration/configuration-overview.md)

