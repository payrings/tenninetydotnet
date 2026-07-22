# 10/90 .NET - Hybrid Frontier/Local AI Development Framework
## Machine Setup Guide (one-time per machine)

An open architecture and framework for software engineering teams building C# and .NET systems with coding agents. It separates system design from mechanical execution: a high-reasoning cloud frontier model writes the specification once, two free local open-weight models handle routine implementation and automated peer review, and a deterministic test suite acts as an automated quality gate. By diminishing reliance on pay-per-token frontier models, this approach drastically reduces operational costs and enables simple setups using local AI models.

The documentation is split in two, mirroring the framework's own lifecycle. **This document (Phases 0–8)** installs and verifies everything that lives on the machine and is shared by every project. These shared components include the GPU runtime, inference server, Docker images, agent profiles, frontier access, and host tooling. **`WORKING_GUIDE.md` (Phases 9–13)** covers the work repeated for each project, including scaffolding, specification installation, the module loop, and human review. Complete this document once; return to the working guide for every new project.

## What is in this repository

This repository is both a linear reference guide and an extractable starter kit,
so you copy pre-tested files rather than pasting hundreds of lines of script out
of documentation blocks. The authoritative map of every file and folder lives in
the repository `README.md`; it is not repeated here to avoid the two drifting
apart. In short: `SETUP_GUIDE.md` (this document) is the one-time machine setup
(Phases 0–8); `WORKING_GUIDE.md` is the per-project operational guide (Phases
9–13); `starter-kit/` holds the executable template you copy from; `examples/`
holds sample configuration such as the `llama-swap` serving config
(`examples/config.yaml`, Phase 4); and `docs/` holds supplementary reference
material (the framework explainer, the developer quickstart, and the
scaling-to-medium-projects design note). See the README for the full table.

## How to use this guide

This guide covers the entire deployment lifecycle in numbered phases. Depending on how you prefer to set up your environment, you can follow the implementation steps in one of two ways:
1. **Using the starter kit (recommended):** Whenever the guide references creating an operational file, such as the bash orchestrator (`dev.sh`), the Roslyn signature checker (`check_signatures.csx`), the Docker container definitions, or the Git pre-commit hooks, do not write them by hand. Copy the corresponding pre-tested file directly from the `starter-kit/` directory of this repository into your working directory and apply the executable permissions shown.
2. **Manual creation:** If you are adapting the scripts to a different language or custom infrastructure, every phase explains the exact mechanics of the underlying code so you can modify the implementation deliberately. Every time the text instructs you to open a terminal, that command blocks the foreground process unless explicitly stated otherwise; open a new terminal tab or window when instructed to proceed to the next phase.

### Path conventions used throughout

Three fixed locations appear in every phase. Establish them now so every `cp` command in the guide resolves unambiguously:

| Path | Meaning |
|---|---|
| `~/tenninetydotnet` | Your local clone of **this repository**. All `cp starter-kit/...` and `cp examples/...` commands in this guide are run **from this directory**. |
| `~/project` | Your workspace parent directory (used by the working guide; one per project). |
| `~/project/workspace` | The project workspace itself – the directory the agents see, and the root of a project's Git repository (created in the working guide, Phase 9). |

Clone the repository first (you will be reminded again in Phase 1):

```bash
git clone https://github.com/payrings/tenninetydotnet.git ~/tenninetydotnet
```

**A note on shells:** Phase 0 assumes the Fish shell, and every code block in this guide runs correctly in Fish. Blocks fenced as `fish` use Fish-only builtins (`fish_add_path`, `set -Ux`) and will **not** work in Bash. Blocks fenced as `bash` use POSIX-compatible syntax (`$(...)` command substitution) and run identically in both shells.

## Prerequisites and system compatibility

This framework relies on a hybrid execution model. While the automation scripts, container boundaries, and quality gates are completely hardware-agnostic, running the local coding and review loop at zero marginal cost requires a baseline local compute setup.

### Target setup

To run both the local Coder and Reviewer models on-premises without cloud API costs, your system should meet the following specifications:

- **Hardware and compute:**
    - **GPU:** A discrete GPU with **at least 24 GB of VRAM** (e.g., AMD Radeon RX 7900 XTX, NVIDIA RTX 3090 / 4090, or Apple Silicon with equivalent unified memory). 24 GB is the strict boundary required to load 24B–27B parameter models with a 65,536-token context window without out-of-memory errors.
    - **Storage:** ~30 GB of free disk space for model weights, container images, and language SDKs.

- **Software stack:**
    - **OS:** The step-by-step commands in this guide are written and tested for **Arch-based Linux (CachyOS)** on an **AMD RX 7900 XTX**. They use Arch-specific tooling throughout — `pacman`, the AUR (`paru`), ROCm, `ufw`, and the Fish shell. The *architecture* (governance hooks, container sandboxing, deterministic gates) is OS- and hardware-agnostic, but the concrete commands are not: on macOS, other Linux distributions, or Windows/WSL2 you will need to translate package installs and the GPU runtime yourself (e.g. Homebrew + Metal on macOS, `apt` + CUDA on Ubuntu/NVIDIA, skipping the ROCm phase where it doesn't apply). Treat this guide as a reference implementation to adapt, not a portable script.
    - **Containerisation:** Docker Engine with non-root user mapping and volume-mount permissions.
    - **Local inference server:** A backend capable of memory-resident model swapping (e.g., `llama-swap`, `llama.cpp`, or `Ollama`) configured to bind to port `8090`.
    - **Host tooling:** Git, Node.js (v22+), Python 3.11+, and your target engineering SDK (.NET 10.x.x, Node, Python, etc.).
    - **Cloud access:** An OpenRouter API key (or equivalent OpenAI-compatible endpoint) with access to a high-reasoning frontier model (e.g., Claude Opus) for specification authoring (working guide, Phase 10) and structured escalation.

### What works regardless (platform and hardware agnostic)

Even if your setup differs from the target hardware above, such as developing on a laptop without a discrete GPU, using a different operating system, or targeting a different programming language, the core *architecture* remains fully functional — though on a non-Arch platform you will have to translate the concrete install commands in this guide (see the OS note above). What is portable is the design:
- **The mechanical governance and hooks:** The pre-commit verification scripts and AST signature-drift rules execute entirely on the host CPU and work across all OS environments.
- **Container sandboxing:** The zero-trust container boundaries (`cline-sandboxed` and `test-runner`), read-only bind mounts (`:ro`), and locked dependency manifests will enforce supply-chain security on any machine running Docker.
- **Deterministic test gates:** The rule of _"Zero AI at test runtime"_ holds universally. The host-driven test orchestration, contract testing, and golden-fixture harnesses rely purely on standard compiler SDKs.
- **Flexible compute routing:** If your local machine lacks 24 GB of VRAM, you can easily shift the 10/90 compute ratio. The orchestration scripts can be pointed to smaller local models (e.g., 8B parameter models), CPU/Vulkan fallbacks, or even external cloud API endpoints for the coding and review passes without altering the repository's rules or workflow.

---
### A note on software staleness

Package names, container image tags, and command-line interface flags for fast-moving projects, including ROCm, `llama.cpp`, `llama-swap`, Cline, OpenRouter's model catalogue, and the .NET SDK, shift regularly. The configurations in this repository reflect the current state as of mid-2026. If a package build fails or a specific runtime flag is rejected, check `--help` on the local binary or consult the upstream project documentation.

---

## High-level architecture

```
┌──────────────────────┐        one-time authoring        ┌──────────────────────────┐
│  Frontier model      │ ───────────────────────────────▶│  Rules + skills under    │
│  (via OpenRouter –   │       + on-demand escalation     │  .cline/                 │
│  model chosen by you)│     (+ --write-code tier for     │                          │
└──────────────────────┘      genuinely stuck pieces)     └──────────────────────────┘
                                                                      │
                                                                      ▼
                                                          ┌──────────────────────────┐
                                                          │  dev.sh orchestrator     │
                                                          │  (host-side bash script) │
                                                          │  Write → Review → Test   │
                                                          │  + broadcast, reset,     │
                                                          │    status                │
                                                          └──────────────────────────┘
                                                                      │
                                                                      ▼
                                                          ┌──────────────────────────┐
                                                          │  Cline CLI, inside a     │
                                                          │  Docker sandbox          │
                                                          │  (tests/contracts and    │
                                                          │   tests/fixtures :ro)    │
                                                          └──────────────────────────┘
                                                                      │
                                                                      ▼
                                                          ┌──────────────────────────┐
                                                          │  llama-swap (host)       │
                                                          │  routes by model name    │
                                                          │  (tested quantisations)  │
                                                          └────────┬────────┬────────┘
                                                                   │        │
                                                     "qwen-coder"  │        │  "devstral-reviewer"
                                                                   ▼        ▼
                                                     ┌──────────────────┐ ┌───────────────────┐
                                                     │ Qwen3.6-27B      │ │ Devstral Small 2  │
                                                     │ (Coder)          │ │ (Reviewer)        │
                                                     └──────────────────┘ └───────────────────┘
                                            only one process alive at a time – llama-swap
                                            terminates the previous model, it does not idle in RAM
                                            (24 GB VRAM budget cannot fit both)
```

---

## Phase 0 – Prerequisites checklist

- [ ] CachyOS fully updated
- [ ] Around 30 GB free disk space for model weights, Docker images and the .NET SDK
- [ ] An OpenRouter account and API key (openrouter.ai/keys)
- [ ] BIOS: confirm the RX 7900 XTX is the primary GPU
- [ ] Konsole terminal running Fish shell
- [ ] Node.js 22+ available (required for the Cline CLI installed in Phase 6)
- [ ] This repository cloned to `~/tenninetydotnet` (see *Path conventions* above)

---

## Phase 1 – Base system preparation

Open a terminal. This phase only requires a single terminal window.

```bash
sudo pacman -Syu
sudo pacman -S --needed base-devel git wget curl cmake ninja \
  unzip jq nodejs npm ripgrep nano
```

If you have not already cloned this repository, do it now. Every `cp starter-kit/...` and `cp examples/...` command in later phases is run from inside this clone:

```bash
git clone https://github.com/payrings/tenninetydotnet.git ~/tenninetydotnet
```

Install the .NET SDK 10.0:

```bash
sudo pacman -S dotnet-sdk dotnet-runtime
```

Verify the installation. This check is deliberately fail-fast: the framework
targets `net10.0` everywhere, and a wrong-version host SDK will otherwise
scaffold subtly incorrect projects in the working guide before anything
visibly breaks.

```fish
dotnet --version | grep -q '^10\.' \
  && echo "OK: .NET 10 SDK present" \
  || echo "STOP: .NET 10 SDK not found – install dotnet-sdk 10 before continuing"
```

The terminal should print `OK: .NET 10 SDK present`. If it prints `STOP: ...`,
install a .NET 10 SDK before continuing; do not proceed on .NET 9 or earlier.
The per-project `global.json` created in the working guide (Phase 9) enforces
this same floor for every `dotnet` command in a workspace, but that file does
not exist yet at this stage, so this manual check is what guards the initial
install.

**Fix Python pathing now:** several phases ahead use `pip install --break-system-packages` (Phase 8). Since CachyOS's system Python is externally managed, pip responds by installing to `~/.local/bin` instead, and that directory isn't on fish's `$PATH` by default, so any tool that ships a command (like `pre-commit`) installs successfully but then can't be found. Fix this permanently now:

```fish
fish_add_path ~/.local/bin
```

This is permanent; it takes effect in every terminal from now on.

Install an AUR helper if you don't already have one:

```fish
which paru
```

If that prints a valid path, skip ahead. If it returns "not found", build it from source:

```fish
git clone https://aur.archlinux.org/paru.git /tmp/paru
cd /tmp/paru
makepkg -si
cd ~
```

---

## Phase 2 – ROCm / GPU setup for the RX 7900 XTX

In the same terminal window, install the AMD ROCm runtime and assign your user to the render video groups.

```bash
sudo pacman -S rocm-hip-sdk rocm-hip-runtime rocm-hip-libraries rocm-opencl-sdk rocminfo
sudo usermod -aG render,video "$USER"
```

**Reboot your computer now**. Group membership changes do not apply until after a full system restart.

Once logged back in, open a terminal and verify the GPU is detected:

```bash
rocminfo | grep -A5 "Agent 2"
rocm-smi
```

**Known issue on Arch-based distros:** the RX 7900 XTX (`gfx1100`) has confirmed reports of not being detected on Arch-based systems, including CachyOS, falling back to CPU. If your GPU does not appear in the runtime list:

1. Confirm you rebooted after the group change, not just re-logged in.
2. Pin ROCm to the discrete card: `set -x HIP_VISIBLE_DEVICES 0`
3. Force the GFX architecture version: `set -x HSA_OVERRIDE_GFX_VERSION 11.0.0`
4. Fall back to the Vulkan backend in `llama.cpp` if ROCm remains unresponsive.

---

## Phase 3 – Build llama.cpp with HIP and ROCm support

**Option A – AUR package (recommended):**

```bash
paru -S llama.cpp-hip
```

If paru displays multiple providers, select the provider `llama.cpp-hip` for the stable version, otherwise `llama.cpp-hip-git` if you prefer the latest version.

**Option B – build from source, only if Option A fails:**

```bash
git clone https://github.com/ggml-org/llama.cpp
cd llama.cpp
env HIPCXX="$(hipconfig -l)/clang" HIP_PATH="$(hipconfig -R)" \
    cmake -S . -B build-rocm -DGGML_HIP=ON -DAMDGPU_TARGETS=gfx1100 -DCMAKE_BUILD_TYPE=Release
cmake --build build-rocm --config Release -j "$(nproc)"
```

**Verify either option worked.** No model weights have been downloaded yet at this point (that happens automatically in Phase 4), so verification comes in two tiers:

**Quick device check (no model required):**

```bash
llama-server --list-devices
```

Look for `ROCm0: AMD Radeon RX 7900 XTX` (or your GPU) in the device list. If your build predates `--list-devices`, run `llama-server --help` and confirm the binary was built with the HIP backend.

**Full benchmark check (optional – downloads a small ~400 MB test model):**

```bash
curl -L -o /tmp/test-model.gguf \
  "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf"
llama-bench -m /tmp/test-model.gguf -ngl 999
rm /tmp/test-model.gguf
```

Look for `found 1 ROCm devices: Device 0: AMD Radeon RX 7900 XTX, gfx1100` in the output. If the device check already showed your GPU, you can safely skip the benchmark and proceed; the production models in Phase 4 will exercise the same code path on first load.

---

## Phase 4 – Install llama-swap and launch both local models

```bash
paru -S llama-swap-bin
mkdir -p ~/llama-swap
```

### 4.1 – Write the serving config

Copy the sample configuration from this repository's `examples/` directory into your local serving folder (run from your clone of this repo):

```bash
cd ~/tenninetydotnet
cp examples/config.yaml ~/llama-swap/config.yaml
```

### 4.2 – Create the model configuration

Open the configuration file and make changes (if necessary):

```bash
nano ~/llama-swap/config.yaml
```

Save the file:

1. Press `Ctrl+O`.
2. Press Enter to confirm the filename.
3. Press `Ctrl+X` to exit nano.

Verify that no **active** (non-comment) line contains an unreplaced `<...>`
placeholder. Comment lines beginning with `#` are documentation (they explain
the pinning options and legitimately contain tokens like `<repo>`), so exclude
them:

```bash
grep -vE '^\s*#' ~/llama-swap/config.yaml | rg -n '<[^>]+>'
```

This command should produce no output. (There is no `@<sha>` revision syntax in
llama.cpp — see the pinning note below — so nothing of that form should appear
on an active line either.)

Display the configured model commands:

```bash
rg -n -A 12 'qwen-coder:|devstral-reviewer:' \
  ~/llama-swap/config.yaml
```

Confirm the following:

* `qwen-coder` uses `unsloth/Qwen3.6-27B-MTP-GGUF:Q4_K_M`.
* `devstral-reviewer` uses `unsloth/Devstral-Small-2-24B-Instruct-2512-GGUF:UD-Q4_K_XL`.
* Qwen contains `-ngl 999`.
* Devstral does **not** contain `-ngl 999`.

Devstral leaves GPU-layer selection unset so that `llama-server` can fit the model to the available VRAM. Forcing `-ngl 999` for Devstral can cause the model process to terminate with an out-of-memory error.

**On reproducible pinning.** The `-hf <repo>:<quant>` form above always pulls the
*current* contents of that Hugging Face repo; llama.cpp has no commit-SHA
argument for `-hf`. That means the convenient form is not reproducibly pinned: if
the repo owner re-uploads the GGUF, your served model can change without any
config edit. For true reproducibility, download the exact GGUF once, record its
SHA-256, and serve it by local path with `--model` (both entries in
`examples/config.yaml` show the commented `--model` alternative):

```bash
hf download unsloth/Qwen3.6-27B-MTP-GGUF Qwen3.6-27B-MTP-Q4_K_M.gguf \
  --local-dir ~/models/qwen-coder
sha256sum ~/models/qwen-coder/Qwen3.6-27B-MTP-Q4_K_M.gguf | tee -a ~/models/MODELS.sha256
```

Then replace the `-hf` line in the model's `cmd:` with the corresponding
`--model /absolute/path.gguf` line, keeping every other flag. Commit
`MODELS.sha256` so a later `sha256sum -c` detects any drift. If you accept the
convenience of `-hf`, at least record the checksums of the downloaded files the
first time so you can detect a silent upstream change.

The next section starts llama-swap and tests both models with real completion requests. Listing a model through `/v1/models` alone does not prove that the model can load successfully.

### 4.3 – Start llama-swap and prove both models actually load

Start the server in the foreground for this first run so you can watch it load
weights and see any error directly. Bind it to the Docker bridge gateway
(`172.17.0.1`, the address containers reach the host on) rather than `0.0.0.0`:
this keeps the unauthenticated model endpoint off your LAN while still letting
the agent containers reach it. Confirm the gateway with `ip addr show docker0`
if your bridge differs.

```bash
llama-swap --config ~/llama-swap/config.yaml --listen 172.17.0.1:8090
```

Leave that terminal running. **Open a second terminal** for the checks below.
(For the host-side `curl` checks, reach it at `172.17.0.1:8090`; from inside a
container it is `host.docker.internal:8090` as before.)

First confirm both models are advertised:

```bash
curl -s http://172.17.0.1:8090/v1/models | jq -r '.data[].id'
```

You should see `qwen-coder` and `devstral-reviewer`. That only proves they are
*configured*, not that they *load* — a wrong quant, a bad path, or insufficient
VRAM only surfaces on a real completion. Send one to each model. The first call
per model may take a while (llama-swap downloads/loads the weights and evicts the
other model, since both cannot be resident in 24 GB at once):

```bash
# Coder
curl -sS http://172.17.0.1:8090/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"qwen-coder","messages":[{"role":"user","content":"Reply with exactly OK."}],"max_tokens":8}' \
  | jq -r '.choices[0].message.content'

# Reviewer (this triggers a model swap)
curl -sS http://172.17.0.1:8090/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"devstral-reviewer","messages":[{"role":"user","content":"Reply with exactly OK."}],"max_tokens":8}' \
  | jq -r '.choices[0].message.content'
```

Each call must return a normal completion (the content will contain `OK`). If a
call hangs indefinitely, or the foreground llama-swap log shows `upstream command
exited prematurely` or repeated HTTP 500s, the model failed to start — fix
`~/llama-swap/config.yaml` (see the machine-level troubleshooting appendix for
context-size and Devstral fallbacks) and retry before continuing. Only once both
models return a completion is the serving layer verified.

Once both succeed, stop the foreground server with `Ctrl+C`; the next section
makes it start automatically instead.

### 4.4 – Optional but recommended – make it survive reboots

```bash
mkdir -p ~/.config/systemd/user
nano ~/.config/systemd/user/llama-swap.service
```

```ini
[Unit]
Description=llama-swap (dual local model server)
After=network.target

[Service]
ExecStart=/usr/bin/llama-swap --config %h/llama-swap/config.yaml --listen 172.17.0.1:8090
Restart=on-failure

[Install]
WantedBy=default.target
```

The unit binds to `172.17.0.1` (the Docker bridge gateway) rather than
`0.0.0.0`. llama-swap has no authentication, so binding it to all interfaces
would expose your GPU inference endpoint to every machine on your LAN. Binding to
the bridge gateway keeps it reachable by the agent containers (as
`host.docker.internal`) and by host-side `curl` at `172.17.0.1:8090`, but not
from the wider network. If `ip addr show docker0` shows a different gateway,
substitute it here and in the Phase 4.3 commands.

Confirm the `llama-swap` binary path matches your system. If `which` prints something other than `/usr/bin/llama-swap`, update the `ExecStart=` binary path to match:

```bash
which llama-swap
```

Then enable the service:

```bash
systemctl --user daemon-reload
systemctl --user enable --now llama-swap.service
systemctl --user status llama-swap.service
```

---

## Phase 5 – Docker setup and firewall configuration

In your second terminal window, install Docker and configure your user groups.

```bash
sudo pacman -S --needed docker docker-buildx
sudo systemctl enable --now docker.socket
sudo usermod -aG docker "$USER"
```

**Log out and back in** to apply the Docker group membership. Then verify:

```bash
docker run hello-world
docker buildx version
```
Both commands should complete successfully before continuing.

**CachyOS-specific issue:** CachyOS ships with Uncomplicated Firewall (`ufw`) active by default with a deny-incoming policy. Traffic from a container to a service published on the host itself (as opposed to routed through the host to the internet) arrives on the `docker0` bridge as *incoming* traffic from ufw's perspective, so ufw's default policy silently drops it. Docker itself is not the blocker; the firewall is.

To allow container sandboxes to communicate with `llama-swap` on port `8090`, check first if the rule is active:

```bash
sudo ufw status verbose
```

If `ufw` is active but you don't see a rule mentioning port `8090`:

```bash
sudo ufw allow from 172.17.0.0/16 to any port 8090
```

`172.17.0.0/16` is Docker's default bridge subnet. Confirm it matches yours with `ip addr show docker0` if you want to be sure.

**Tightening agent egress (recommended).** The rule above lets any container on
the default bridge reach port 8090, and by default a container can also reach the
wider internet. The agent container never needs the internet — only llama-swap.
To harden this, run the orchestrator in restricted-network mode, which puts agent
containers on a dedicated bridge (`tenninety-agent`) instead of the shared
default bridge:

```fish
set -Ux DEV_AGENT_NETWORK restricted
```

`dev.sh` then creates and uses that network automatically. Because it is a named
network with its own subnet, you can write firewall rules that apply only to
agent containers. Find its subnet after the first restricted run:

```bash
docker network inspect tenninety-agent \
  --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
```

Then allow that subnet to reach only 8090 and deny its other outbound traffic
(adjust the subnet to what the command printed):

```bash
sudo ufw allow from 172.20.0.0/16 to 172.17.0.1 port 8090
sudo ufw deny out on br-<id> from 172.20.0.0/16
```

`br-<id>` is the host interface for the `tenninety-agent` bridge (visible in
`ip addr`). This blocks the agent from exfiltrating the mounted workspace while
leaving llama-swap reachable. If you skip this, at least keep the model endpoint
bound to the bridge gateway (Phase 4) rather than `0.0.0.0` so it is not exposed
to your LAN.

---

## Phase 6 – Build the two container images

The framework uses two isolated Docker containers:

- **`cline-sandboxed`** – executes the AI coding agent. Needs Node (for Cline) and git (so the agent can run `git diff` to inspect its own changes, but it does **not** commit; the orchestrator owns all Git state). It does **not** get the .NET SDK, test tools, Docker access, or any API keys.
- **`test-runner`** – executes deterministic builds and tests. Contains the .NET 10 SDK and mounts the workspace read-write, but has no LLM tooling. Test execution is split in two: `dotnet restore --locked-mode` runs with network access to populate the NuGet cache (no test code runs in this step), then `dotnet build` and `dotnet test` run in a second invocation with `--network=none`, so the arbitrary Coder-written code that executes during build and test has no network at all and cannot exfiltrate the workspace or reach a model.

Both images run as a **non-root user whose UID/GID match your host user**, baked in at build time. This keeps agent-created files (and `bin/`, `obj/`, test output) owned by you on the host, keeps file permissions meaningful, and, since the container user isn't root, a bind mount marked `:ro` can't be written through with root's mode-bit override. The real immutability boundary remains the Docker `:ro` mount; non-root is what stops the guarantees from being quietly defeated. Because the user has a real home directory in the image, the Cline profile mounts cleanly at `$HOME/.cline` with nothing else to work around.

The images are **machine-global**: Docker stores them by tag, every project on this machine uses the same two images, and you only rebuild if a Dockerfile changes. Build them directly from your clone of this repository. No project directory is needed at this stage. The `$(...)` substitutions below work identically in Fish (3.4+) and Bash:

```bash
cd ~/tenninetydotnet
docker build \
  --build-arg HOST_UID=$(id -u) \
  --build-arg HOST_GID=$(id -g) \
  -t cline-sandboxed -f starter-kit/Dockerfile.cline .

docker build \
  --build-arg HOST_UID=$(id -u) \
  --build-arg HOST_GID=$(id -g) \
  -t test-runner -f starter-kit/Dockerfile.test .
```

Verify both builds returned valid binaries:

```bash
docker run --rm cline-sandboxed --version
docker run --rm --entrypoint git cline-sandboxed --version
docker run --rm test-runner dotnet --version
```

The `test-runner` command must print `10.x.x`. The other two commands must print valid Cline and Git versions.

---

## Phase 7 – Point Cline at both local models

```bash
mkdir -p ~/.cline-coder ~/.cline-reviewer

docker run --rm -it \
  -v ~/.cline-coder:/home/node/.cline \
  --add-host host.docker.internal:host-gateway \
  cline-sandboxed auth
```

This opens an interactive menu. **Do not pick any of the first three options.**

1. Press the down arrow three times to reach **"Bring your own provider,"** then press Enter.
2. From the provider list, choose **"OpenAI Compatible."**
3. Fill in the three fields **exactly in this order**:
   - Base URL: `http://host.docker.internal:8090/v1`
   - Azure API Version: leave this **empty**
   - API key: `local-llm`

4. Press Enter to save. Set **Model ID** to `qwen-coder`.

Repeat the configuration for the reviewer profile:

```bash
docker run --rm -it \
  -v ~/.cline-reviewer:/home/node/.cline \
  --add-host host.docker.internal:host-gateway \
  cline-sandboxed auth
```

Same steps, but set **Model ID** to `devstral-reviewer`.

**Verify that the profile is readable by the non-root container user.** Cline may store its configuration beneath a `data` subdirectory, so recursively list the mounted profile rather than checking only its top level:

```fish
docker run --rm \
  -v ~/.cline-coder:/home/node/.cline \
  --entrypoint find \
  cline-sandboxed /home/node/.cline -maxdepth 5 -ls
```

The command should list `/home/node/.cline`, its `data` directory, and any files created by the `auth` step. The entries should be owned by `node:node`, and there should be no `Permission denied` errors.

Repeat the check for the reviewer profile:

```fish
docker run --rm \
  -v ~/.cline-reviewer:/home/node/.cline \
  --entrypoint find \
  cline-sandboxed /home/node/.cline -maxdepth 5 -ls
```

If either profile contains only empty directories and no files, rerun its `cline-sandboxed auth` command before continuing.

---

## Phase 8 – Wire up the frontier model and escalation script

### 8.1 – Get an OpenRouter key and choose a model

1. Sign up at openrouter.ai and create a key at openrouter.ai/keys; it starts with `sk-or-v1-`.
2. Browse openrouter.ai/models and copy the exact slug of whichever model you want handling escalations.

Store the key in a permission-restricted `.env` that only the escalation tool
reads, rather than a fish universal variable. `set -Ux` writes the value in
plaintext to `~/.config/fish/fish_variables` **and** exports it into the
environment of every process your shell launches — a wide blast radius for a
paid API secret. A mode-600 `.env` read only by `escalate.py` is tighter:

```bash
mkdir -p ~/.config/tenninety
umask 077
printf 'OPENROUTER_API_KEY=sk-or-v1-...\nFRONTIER_MODEL=z-ai/glm-5.2\n' \
  > ~/.config/tenninety/.env
chmod 600 ~/.config/tenninety/.env   # belt and braces
```

`escalate.py` looks for the key in this order: an exported `OPENROUTER_API_KEY`
in the environment, then `$TENNINETY_ENV`, then `./.env`, then
`~/.config/tenninety/.env`. It never overrides a value you exported explicitly,
and it warns if the `.env` is group/world readable. Keep any project-local
`.env` out of Git (add it to `.gitignore`).

**To switch models later:** edit `FRONTIER_MODEL` in that `.env` (or export it
for one session):

```bash
sed -i 's#^FRONTIER_MODEL=.*#FRONTIER_MODEL=anthropic/claude-opus-4.8#' ~/.config/tenninety/.env
```

### 8.2 – Install Python dependencies and the escalation script

The escalation script itself is written in Python; it's a host-side tool, not project code, so the language of the project doesn't affect it. It needs Python 3 and pip, which ship with CachyOS. Confirm and install the OpenAI SDK:

```bash
python3 --version   # expect 3.11+
python3 -m pip --version
pip install openai --break-system-packages
```

**Note**: If `pip` isn't found, install it with `sudo pacman -S python-pip`. Recall from the Phase-1 note that `--break-system-packages` installs commands to `~/.local/bin`; run `fish_add_path ~/.local/bin` if a freshly installed command isn't found.

### 8.3 – Smoke test the escalation path

Verify the cloud frontier model is responding before you ever rely on it during live development. The escalation script expects to run inside a Git repository. Your clone of this repository already is one, so no project workspace is required. Run it in `--dry-run` mode: it exercises the full path (loads your key, builds the prompt from the current diff, calls the frontier model, writes the result) but saves the artefact to a temp directory and does **not** touch `.escalations.json`, so there is nothing to clean up afterwards:

```bash
cd ~/tenninetydotnet
python starter-kit/scripts/escalate.py smoke-test --dry-run
```

Here `smoke-test` is just the module-id argument; with no test-log file the script triages the current (empty) diff, which is enough to confirm the key works and the model responds. A valid triage response printed to the terminal — and a `Saved to /tmp/tenninety-escalate-.../escalation-notes.md` line — means the escalation path is live. Because it was a dry run, no artefacts are left in the clone and the escalation counter is untouched.

### 8.4 – Install host orchestration tooling

These three tools are used by every project's pre-commit hooks and signature checks, so install them once at machine level rather than per project:

```fish
dotnet tool install -g dotnet-script
fish_add_path ~/.dotnet/tools
pip install pre-commit --break-system-packages
```

`dotnet-script` gives the signature-drift hook its Roslyn AST parser; `pre-commit` is the hook runner each project wires up in Phase 9.3 of the working guide. Both `fish_add_path` calls in this guide (this one and Phase 1's `~/.local/bin`) are permanent universal changes; they never need repeating.

**Warm the `dotnet-script` NuGet cache now.** The signature-drift script pulls its Roslyn packages (`Microsoft.CodeAnalysis.CSharp` and its dependency `Microsoft.CodeAnalysis.Common`) at first run via a `#r "nuget:"` directive. That fetch populates a `dotnet-script`-specific cache that a normal project `dotnet restore` does **not** fill, so without this step your first `pre-commit run` fails with *"package ... was not found in the global NuGet cache(s)"*. The version warmed here **must exactly match** the `#r "nuget: Microsoft.CodeAnalysis.CSharp, <version>"` line at the top of `scripts/check_signatures.csx` (currently `5.0.0`, the Roslyn release that parses C# 14 / .NET 10); if you bump one, bump the other, or the offline cache will miss and the hook will fail on first use. Trigger the download once, at machine level, while you have network access:

```fish
printf '#r "nuget: Microsoft.CodeAnalysis.CSharp, 5.0.0"\nSystem.Console.WriteLine("roslyn cache warmed");\n' > /tmp/warm-roslyn.csx
dotnet script /tmp/warm-roslyn.csx
rm /tmp/warm-roslyn.csx
```

It should print `roslyn cache warmed`. If it instead reports the package was not found in cache, re-run the middle line with `--no-cache` (`dotnet script --no-cache /tmp/warm-roslyn.csx`), which forces the fetch. Once cached, every project's signature-drift hook resolves offline.

---

## Setup verification checklist

Run this once at the end of machine setup. Every item must pass before starting your first project with `WORKING_GUIDE.md`; none of them ever needs re-checking per project (only after OS, driver, or image changes).

- [ ] `rocminfo` shows `gfx1100` / RX 7900 XTX
- [ ] `llama-swap` lists both `qwen-coder` and `devstral-reviewer` at `http://172.17.0.1:8090/v1/models` (the bridge-gateway bind, not `0.0.0.0`), **and** each returns a real completion from `/v1/chat/completions` (Phase 4.3) — listing alone does not prove a model loads
- [ ] each model in `~/llama-swap/config.yaml` is served either by `-hf <repo>:<quant>` (convenient, not reproducibly pinned) or by a local `--model <path>.gguf` whose SHA-256 you have recorded (reproducibly pinned); no active line contains an unreplaced `<...>` placeholder
- [ ] context window is 65536 for both models
- [ ] both images build: `docker run --rm cline-sandboxed --version`, `docker run --rm --entrypoint git cline-sandboxed --version`, and `docker run --rm test-runner dotnet --version` all print versions
- [ ] both images run as non-root and create host-user-owned files: a file created inside a throwaway container's mounted directory is owned by you, not root
- [ ] a throwaway container with `~/.cline-coder` mounted at `/home/node/.cline` can list the profile's files without permission errors and can reach `http://host.docker.internal:8090/v1/models`
- [ ] a throwaway container with `~/.cline-reviewer` mounted resolves to `devstral-reviewer`, not `qwen-coder`
- [ ] the agent container sees only its mounted directories (not your host home, no Docker socket, no API keys)
- [ ] `OPENROUTER_API_KEY` is available to `escalate.py` (via a mode-600 `.env` or an exported var) and `FRONTIER_MODEL` is set, and the Phase 8.3 `--dry-run` smoke test returned a real triage response without leaving artefacts in the clone
- [ ] `dotnet script --version` and `pre-commit --version` both print versions in a fresh terminal
- [ ] the `dotnet-script` Roslyn cache is warmed (the Phase 8.4 warm step printed `roslyn cache warmed`), so the signature-drift hook resolves without network on first project use

**Machine setup is complete.** For each project you build on this machine, follow `WORKING_GUIDE.md` from Phase 9.

---

## Appendix: Machine-level troubleshooting

| Symptom                                                                           | Fix                                                                                                                              |
| --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| `fish: Unknown command: <tool>` right after `pip install --break-system-packages` | installed to `~/.local/bin`, not on `$PATH` – run `fish_add_path ~/.local/bin`                                                   |
| `cp: cannot stat 'starter-kit/...'`                                               | you're not inside your clone of this repo – `cd ~/tenninetydotnet` first (see *Path conventions*)                                 |
| `(id -u)` syntax error in bash                                                    | the guide's build commands use `$(id -u)`, which works in bash and fish alike – check you copied the current version             |
| ROCm doesn't see the GPU on CachyOS                                               | groups + reboot → `HIP_VISIBLE_DEVICES` → `HSA_OVERRIDE_GFX_VERSION` → fall back to Vulkan                                       |
| Cline container can't reach llama-swap                                            | confirm `--add-host host.docker.internal:host-gateway`; confirm llama-swap bound to `172.17.0.1:8090` (the bridge gateway); check ufw rule for port 8090; in restricted mode confirm the `tenninety-agent` egress rule still allows 8090 |
| `docker run curlimages/curl ... host.docker.internal:8090` times out              | classic ufw rule missing – `sudo ufw status verbose` and check for port 8090                                                     |
| `request exceeds available context size (32768 tokens)`                           | set `--ctx-size 65536` in `~/llama-swap/config.yaml`                                                                             |
| Devstral review output looks garbled or loops                                     | fall back to `unsloth/Devstral-Small-2507-GGUF`                                                                                  |

---
