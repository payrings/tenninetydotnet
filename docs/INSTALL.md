# Installing 10/90 (tenninety) on CachyOS

This guide starts with a quick offline rehearsal and then builds the recommended live
environment: local GPU inference on the physical CachyOS host, with `tenninety`, aider, generated
code, builds, and tests confined to a disposable KVM virtual machine.

Target physical machine:

- CachyOS or another current Arch-based system.
- Fish shell for interactive commands.
- 32 GB RAM and one 24 GB VRAM GPU.
- An Intel or AMD processor with hardware virtualization.
- Internet access for installation, model downloads, NuGet, GitHub, and the Frontier provider.

Simple command blocks work in both fish and bash. Blocks explicitly labelled `bash` use bash
syntax. Fish environment-variable syntax is shown as `set -gx NAME value`; the bash equivalent is
`export NAME=value`.

## Read this first

### The three outcomes

| Outcome | Sections | Result |
| --- | --- | --- |
| A. Offline rehearsal | 1-4 | The framework builds, its tests pass, and a mock project runs without models or keys. |
| B. Host model service | 5-6 | Two different GGUF models are served one at a time through host-only llama-swap. |
| C. Isolated live execution | 7-17 | Agents run as a non-sudo user in a disposable KVM guest with no host mounts or device passthrough. |

Outcome C is the recommended live configuration. Section 18 preserves direct-host execution only
as a clearly non-isolated fallback.

### What the VM boundary does

`tenninety` now implements a Docker sandbox for Coder, Reviewer exploration, restricted Restore,
and Tester commands. Running the framework and its Docker daemon inside a KVM guest adds a second
boundary around the trusted orchestrator, Docker control plane, model tunnel, and any
operator-configured `unsafe-host` compatibility execution. Defense in depth is still recommended.

The recommended topology is:

```text
Physical CachyOS host
  GPU, model files, llama.cpp, llama-swap on 127.0.0.1:8080
                         |
                         | host-initiated SSH reverse tunnel
                         v
Disposable KVM guest
  guest 127.0.0.1:18080 -> host 127.0.0.1:8080
  .NET, git, tenninety, aider, workspace, builds, and tests
```

The VM is a strong practical boundary, not a mathematical guarantee. A guest can still destroy
its own workspace, consume its assigned resources, call the model endpoint, and send guest-visible
data to permitted public HTTPS destinations. KVM/QEMU and llama-swap must remain patched.

### Physical host versus guest

| Physical host | Disposable VM |
| --- | --- |
| GPU drivers and GPU | No GPU passthrough |
| Model files | No model files |
| llama.cpp and llama-swap | .NET SDK and Git |
| KVM, QEMU, libvirt, virt-manager | tenninety and aider |
| Host-only model endpoint `127.0.0.1:8080` | Project workspace and endpoint `127.0.0.1:18080` |
| No project mount | No host filesystem share |

Never attach these to the agent VM:

- VirtioFS, 9p, Samba, NFS, or another host-directory share.
- The host SSH agent, Docker socket, libvirt socket, X11 socket, Wayland socket, or D-Bus socket.
- The GPU, USB devices, physical disks, or PCI devices.
- SPICE clipboard/drag-and-drop integration or USB redirection.

## 0. Requirements

| Requirement | Needed for | Installed where |
| --- | --- | --- |
| .NET 10 SDK and ASP.NET targeting pack | framework and generated project | physical host for rehearsal; VM for live mode |
| Git 2.40 or newer | all framework state and promotion | same OS as tenninety |
| llama.cpp with working Vulkan or CUDA | local inference | physical host only |
| llama-swap | one-GPU model swapping | physical host only |
| Two genuinely different GGUF models | coder and independent reviewer | physical host only |
| aider | live coding-agent process | VM only in the isolated path |
| Frontier-compatible HTTPS API and key | live planning and repair advice | key entered only in the VM session |
| KVM/QEMU, libvirt, virt-manager | live isolation | physical host only |
| About 40 GB free for the two example GGUF files | local model storage | physical host only |
| About 50 GB sparse VM disk | disposable guest | physical host storage |

Mock mode needs only .NET, Git, the framework source, and about 200 MB of build space.

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

## 3. Prepare the IncidentDesk specification

The detailed test specification created for this installation is located on the physical host at:

```text
/home/operator/IncidentDesk/spec.md
```

Do not build IncidentDesk directly in that physical-host folder for the isolated live test. The
file will be transferred into the VM after the secure guest is ready.

---

## 4. Decide whether to continue to live mode

Stop after Section 2 if you only need the mock rehearsal.

Continue when all of these statements are true:

- The Release build and tests pass.
- Hardware GPU inference already works or you are prepared to configure it.
- You have two different model weights, not two aliases for the same weights.
- You have a Frontier endpoint and a dedicated, spending-limited API key.
- You accept that the guest workspace is disposable and may be destroyed by an agent.
- You will not mount any physical-host folder into the VM.

---

## 5. Install local inference on the physical host

All commands in Sections 5 and 6 run on the **physical host**, not inside the VM.

### 5.1 Install llama.cpp and model-download tools

```fish
sudo pacman -Syu --needed llama-cpp uv
uv tool install "huggingface_hub[cli]"
fish_add_path --universal ~/.local/bin

llama-server --version
hf --help
```

GPU notes:

- AMD users normally need `mesa`, `vulkan-radeon`, and a Vulkan-capable llama.cpp build. Verify
  with `vulkaninfo` if llama.cpp cannot find the card.
- NVIDIA users need the matching `nvidia` and `nvidia-utils` packages and a CUDA-capable
  llama.cpp build. The regular Arch package may use Vulkan instead.
- Do not pass the GPU into the agent VM. Only llama.cpp on the physical host needs it.

### 5.2 Install llama-swap

On CachyOS, the simplest path is usually the AUR package:

```fish
paru -S llama-swap-bin
```

Alternatively, download the Linux release from
<https://github.com/mostlygeek/llama-swap/releases>, verify its published checksum, and install
the binary:

```fish
sudo install -m 0755 ./llama-swap /usr/local/bin/llama-swap
```

Verify and record the actual path because the systemd service needs it:

```fish
command -v llama-swap
llama-swap --version
```

The examples below use `/usr/local/bin/llama-swap`. Replace that path with `/usr/bin/llama-swap`
if the AUR package installed it there.

### 5.3 Download and pin two different models

The following pair is suitable for the example topology. Model repositories can change; verify
the exact filename on Hugging Face before running the command.

```fish
mkdir -p ~/Models/qwen-coder ~/Models/devstral-reviewer

hf download \
    unsloth/Qwen3.6-27B-MTP-GGUF \
    Qwen3.6-27B-Q4_K_M.gguf \
    --local-dir ~/Models/qwen-coder

hf download \
    unsloth/Devstral-Small-2-24B-Instruct-2512-GGUF \
    Devstral-Small-2-24B-Instruct-2512-UD-Q4_K_XL.gguf \
    --local-dir ~/Models/devstral-reviewer
```

Record checksums so a later replacement cannot silently change the weights:

```fish
sha256sum \
    ~/Models/qwen-coder/Qwen3.6-27B-Q4_K_M.gguf \
    ~/Models/devstral-reviewer/Devstral-Small-2-24B-Instruct-2512-UD-Q4_K_XL.gguf \
    > ~/Models/MODELS.sha256

sha256sum --check ~/Models/MODELS.sha256
```

`qwen-coder` and `devstral-reviewer` are deliberately different weights. The framework can only
check that configured identifiers differ; you remain responsible for this weight-level check.

---

## 6. Configure host-only llama-swap

### 6.1 Create a local API key

The key protects llama-swap's inference endpoints. It does not stop the authorized coding agent
from using the model; that access is required.

```fish
install -d -m 0700 ~/.config/llama-swap
umask 077
set -l generated_key (openssl rand -hex 32)
printf 'LLAMA_SWAP_API_KEY=%s\n' "$generated_key" \
    > ~/.config/llama-swap/secrets.env
set -e generated_key
chmod 0600 ~/.config/llama-swap/secrets.env
```

### 6.2 Create `~/.config/llama-swap/config.yaml`

```yaml
captureBuffer: 0
globalTTL: 600

macros:
  models_dir: "${env.HOME}/Models"

apiKeys:
  - "${env.LLAMA_SWAP_API_KEY}"

models:
  qwen-coder:
    cmd: |
      /usr/bin/llama-server
      --host 127.0.0.1
      --port ${PORT}
      --model ${models_dir}/qwen-coder/Qwen3.6-27B-Q4_K_M.gguf
      --ctx-size 16384
      --jinja
      -ngl 999
    ttl: 600
    concurrencyLimit: 1

  devstral-reviewer:
    cmd: |
      /usr/bin/llama-server
      --host 127.0.0.1
      --port ${PORT}
      --model ${models_dir}/devstral-reviewer/Devstral-Small-2-24B-Instruct-2512-UD-Q4_K_XL.gguf
      --ctx-size 16384
      --jinja
      -ngl 999
    ttl: 600
    concurrencyLimit: 1
```

If `command -v llama-server` reports a different path, update both commands. Lower
`--ctx-size` or use a smaller quant if model loading exceeds VRAM.

### 6.3 Create a user service

Create the user-service directory:

```fish
install -d -m 0700 ~/.config/systemd/user
```

Create `~/.config/systemd/user/llama-swap.service`:

```ini
[Unit]
Description=llama-swap local model router
After=network-online.target

[Service]
Type=simple
EnvironmentFile=%h/.config/llama-swap/secrets.env
ExecStart=/usr/local/bin/llama-swap --config %h/.config/llama-swap/config.yaml --listen 127.0.0.1:8080
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
```

Use the path returned by `command -v llama-swap` in `ExecStart`.

Start and verify:

```fish
systemctl --user daemon-reload
systemctl --user enable --now llama-swap.service
systemctl --user status llama-swap.service

set -l llama_key \
    (string replace 'LLAMA_SWAP_API_KEY=' '' \
        < ~/.config/llama-swap/secrets.env)

curl --fail --silent \
    -H "Authorization: Bearer $llama_key" \
    http://127.0.0.1:8080/v1/models | jq .

set -e llama_key
```

Confirm the service is not exposed to the LAN:

```fish
ss -ltn '( sport = :8080 )'
```

The listener must be `127.0.0.1:8080`. Stop immediately if it shows `0.0.0.0:8080`,
`*:8080`, the physical LAN address, or an IPv6 wildcard.

---

## 7. Install KVM and libvirt on the physical host

### 7.1 Verify hardware support

```fish
lscpu | grep Virtualization
test -c /dev/kvm; and echo '/dev/kvm is available'
```

The current target machine reports Intel VT-x and has `/dev/kvm`.

### 7.2 Install virtualization packages

```fish
sudo pacman -Syu --needed \
    qemu-desktop libvirt virt-manager dnsmasq iptables openbsd-netcat
```

Do not enable the system-wide `dnsmasq.service`; libvirt launches a private dnsmasq instance for
each virtual network.

Start libvirt:

```fish
sudo systemctl enable --now libvirtd.service
sudo systemctl start virtlogd.service
sudo usermod -aG libvirt $USER
```

Log out of the desktop completely and log back in so the group change takes effect. Never run
`virt-manager` with `sudo`.

Validate:

```fish
id -nG | grep libvirt
sudo virt-host-validate qemu
virsh -c qemu:///system list --all
```

An IOMMU warning does not matter because this guide does not use PCI passthrough.

### 7.3 Start the temporary default network

The default NAT network is used only while the trusted base VM is installed and provisioned.

```fish
virsh -c qemu:///system net-list --all
virsh -c qemu:///system net-autostart default
virsh -c qemu:///system net-start default
```

Skip `net-start` if it is already active.

If `default` does not exist, create `~/default-libvirt-network.xml`:

```xml
<network>
  <name>default</name>
  <forward mode='nat'/>
  <bridge name='virbr0' stp='on' delay='0'/>
  <ip address='192.168.122.1' prefix='24'>
    <dhcp>
      <range start='192.168.122.2' end='192.168.122.254'/>
    </dhcp>
  </ip>
</network>
```

Then define and start it:

```fish
virsh -c qemu:///system net-define ~/default-libvirt-network.xml
virsh -c qemu:///system net-autostart default
virsh -c qemu:///system net-start default
```

---

## 8. Create and provision the base VM

### 8.1 Create two dedicated host SSH keys

These private keys remain on the physical host. They are never forwarded into the guest.

```fish
ssh-keygen -t ed25519 \
    -f ~/.ssh/tenninety-runner \
    -C tenninety-runner

ssh-keygen -t ed25519 \
    -f ~/.ssh/tenninety-tunnel \
    -C tenninety-llama-tunnel
```

Use passphrases. Do not reuse your normal GitHub or login key.

### 8.2 Create the VM in virt-manager

Download a current CachyOS ISO from <https://cachyos.org/download/> and verify its published
SHA-256 checksum before use.

Start virt-manager:

```fish
virt-manager --connect qemu:///system
```

Create a new local-install-media VM with these settings:

| Setting | Value |
| --- | --- |
| Name | `tenninety-agent` |
| Hypervisor | KVM |
| CPUs | 8, host-passthrough if offered |
| Memory | 12,288 MiB |
| Disk | 50 GiB, qcow2, sparse, VirtIO |
| Firmware | Legacy BIOS for this Linux guest |
| Temporary network | `default`, VirtIO NIC |
| Graphics | SPICE display is acceptable for console use |
| Autostart | disabled |

Select **Customize configuration before install** and verify:

- There is exactly one virtual NIC.
- There is no Filesystem device.
- There is no PCI, USB, GPU, or physical-disk passthrough.
- Remove USB Redirector devices.
- Remove the SPICE agent channel if present and do not install `spice-vdagent` in the guest.
- Do not add a shared clipboard or shared folder.

Install CachyOS and create an administrator named `vmadmin`. This account is used only for guest
provisioning and firewall maintenance. Do not enable automatic login.

### 8.3 Install guest packages

Run inside the VM as `vmadmin`:

```fish
sudo pacman -Syu --needed \
    git dotnet-sdk aspnet-runtime aspnet-targeting-pack \
    fish uv openssh ufw curl jq bind openbsd-netcat rsync

sudo systemctl enable --now sshd.service
```

### 8.4 Transfer only the public keys during provisioning

Find the temporary VM address on the physical host:

```fish
virsh -c qemu:///system net-dhcp-leases default
```

Set the address reported for `tenninety-agent`:

```fish
set TEMP_VM_IP 192.168.122.X
```

Replace `X` with the complete address from `net-dhcp-leases`; do not run the following commands
with the literal placeholder.

Copy only public keys to the temporary administrator account:

```fish
scp ~/.ssh/tenninety-runner.pub \
    ~/.ssh/tenninety-tunnel.pub \
    vmadmin@$TEMP_VM_IP:/tmp/
```

### 8.5 Create non-sudo runner and tunnel accounts

Run inside the VM as `vmadmin`:

```fish
sudo useradd --create-home --shell /usr/bin/fish runner
sudo passwd --lock runner

sudo useradd --create-home --shell /usr/bin/bash tunnel
sudo passwd --lock tunnel

sudo install -d -o runner -g runner -m 0700 /home/runner/.ssh

awk '{
  print "from=\"192.168.250.1\",restrict,pty " $0
}' /tmp/tenninety-runner.pub \
  | sudo tee /home/runner/.ssh/authorized_keys > /dev/null

sudo chown runner:runner /home/runner/.ssh/authorized_keys
sudo chmod 0600 /home/runner/.ssh/authorized_keys

sudo install -d -o tunnel -g tunnel -m 0700 /home/tunnel/.ssh

awk '{
  print "from=\"192.168.250.1\",restrict,port-forwarding,permitlisten=\"127.0.0.1:18080\" " $0
}' /tmp/tenninety-tunnel.pub \
  | sudo tee /home/tunnel/.ssh/authorized_keys > /dev/null

sudo chown tunnel:tunnel /home/tunnel/.ssh/authorized_keys
sudo chmod 0600 /home/tunnel/.ssh/authorized_keys

rm /tmp/tenninety-runner.pub /tmp/tenninety-tunnel.pub
```

Both keys are accepted only from the dedicated bridge gateway. The runner key restores PTY access
but keeps agent, X11, socket, and port forwarding disabled. Neither account is added to `wheel`,
`docker`, `libvirt`, or another privileged group.

### 8.6 Install root-owned aider and tenninety

The untrusted runner must not be able to replace the orchestrator, aider executable, Git, or
.NET between attempts. Perform this subsection as `vmadmin`.

Install aider into root-owned tool directories:

```fish
sudo env \
    UV_TOOL_DIR=/opt/uv-tools \
    UV_TOOL_BIN_DIR=/usr/local/bin \
    uv tool install aider-chat

sudo chown -R root:root /opt/uv-tools
sudo chmod -R go-w /opt/uv-tools
sudo chown -h root:root /usr/local/bin/aider

sudo install -d -o root -g root -m 0755 /etc/tenninety
printf '{}\n' | sudo tee /etc/tenninety/aider.conf.yml > /dev/null
sudo chown root:root /etc/tenninety/aider.conf.yml
sudo chmod 0644 /etc/tenninety/aider.conf.yml
```

Clone, validate, and publish tenninety as `vmadmin`:

```fish
cd ~
git clone https://github.com/payrings/tenninetydotnet.git tenninetydotnet
cd ~/tenninetydotnet

dotnet build -c Release
dotnet test -c Release
dotnet publish src/Tenninety.Cli/Tenninety.Cli.csproj \
    -c Release \
    -o ~/tenninety-publish

sudo install -d -o root -g root -m 0755 /opt/tenninety
sudo cp -a ~/tenninety-publish/. /opt/tenninety/
sudo chown -R root:root /opt/tenninety
sudo chmod -R go-w /opt/tenninety
sudo ln -sf /opt/tenninety/tenninety /usr/local/bin/tenninety
sudo chown -h root:root /usr/local/bin/tenninety
```

Open a runner shell only for the runner-specific Git identity and verification:

```fish
sudo -iu runner

git config --global user.name "Your Name"
git config --global user.email "you@example.com"

set -gx PATH /usr/local/bin /usr/bin

aider --version
tenninety --help
dotnet --version
git --version
```

Verify that runner has no passwordless sudo. This command must fail:

```fish
sudo -n true
```

Exit back to `vmadmin` when provisioning is complete. The later live-run instructions set the same
protected PATH before launching tenninety, so files created under runner's home cannot shadow Git,
.NET, aider, or the orchestrator.

---

## 9. Create the dedicated agent network on the physical host

The temporary `default` network permits more access than an untrusted agent should receive. This
section creates a dedicated NAT network and a host-side nftables boundary.

The values below were selected for the current target machine:

| Value | Setting |
| --- | --- |
| Physical uplink | `enp132s0` |
| Physical LAN | `192.168.50.0/24` |
| Existing Docker network | `172.17.0.0/16` |
| Agent network | `192.168.250.0/24` |
| Agent gateway | `192.168.250.1` |
| Agent guest | `192.168.250.10` |
| Agent bridge | `virbr-agent` |
| Fixed VM MAC | `52:54:00:90:10:01` |

### 9.1 Recheck values before applying rules

Run on the physical host:

```fish
ip -4 route get 1.1.1.1
ip -4 route show table all
ip -6 route show table all
ip -brief link
```

The Internet route must still use `enp132s0`, and no route may already use
`192.168.250.0/24`. If either fact changed, stop and update every corresponding value in the
network XML and firewall before continuing.

### 9.2 Define, but do not start, `agent-net`

Create `~/tenninety-agent-net.xml` on the physical host:

```xml
<network ipv6='no' trustGuestRxFilters='no'>
  <name>agent-net</name>
  <forward mode='nat' dev='enp132s0'/>
  <bridge name='virbr-agent'
          stp='on'
          delay='0'
          macTableManager='libvirt'/>
  <port isolated='yes'/>
  <domain name='agent.invalid' localOnly='yes'/>
  <dns forwardPlainNames='no'>
    <forwarder addr='1.1.1.1'/>
    <forwarder addr='9.9.9.9'/>
  </dns>
  <ip address='192.168.250.1' prefix='24'>
    <dhcp>
      <host mac='52:54:00:90:10:01'
            name='tenninety-agent'
            ip='192.168.250.10'/>
    </dhcp>
  </ip>
</network>
```

Define it and deliberately disable autostart:

```fish
virsh -c qemu:///system net-define ~/tenninety-agent-net.xml
virsh -c qemu:///system net-autostart agent-net --disable
virsh -c qemu:///system net-dumpxml agent-net
```

Do not start it until the firewall is loaded.

### 9.3 Create the host nftables boundary

Create the directory and then edit `/etc/nftables.d/tenninety-agent.nft`:

```fish
sudo install -d -m 0755 /etc/nftables.d
sudoedit /etc/nftables.d/tenninety-agent.nft
```

Use this complete file:

```nft
define vmbr = "virbr-agent"
define uplink = "enp132s0"
define gw4 = 192.168.250.1
define guest4 = 192.168.250.10

table inet agent_vm_boundary {
    set blocked4 {
        type ipv4_addr
        flags interval
        elements = {
            0.0.0.0/8,
            10.0.0.0/8,
            100.64.0.0/10,
            127.0.0.0/8,
            169.254.0.0/16,
            172.16.0.0/12,
            192.0.0.0/24,
            192.0.2.0/24,
            192.88.99.0/24,
            192.168.0.0/16,
            198.18.0.0/15,
            198.51.100.0/24,
            203.0.113.0/24,
            224.0.0.0/4,
            240.0.0.0/4
        }
    }

    chain vm_input {
        type filter hook input priority -10
        policy accept

        iifname $vmbr meta nfproto ipv6 counter drop

        iifname $vmbr \
            ip daddr { 192.168.250.1, 192.168.250.255, 255.255.255.255 } \
            udp sport 68 udp dport 67 counter accept

        iifname $vmbr ip saddr != $guest4 counter drop
        iifname $vmbr ct state established,related counter accept

        iifname $vmbr ip daddr $gw4 udp dport 53 counter accept
        iifname $vmbr ip daddr $gw4 tcp dport 53 counter accept

        iifname $vmbr counter drop
    }

    chain vm_forward {
        type filter hook forward priority -10
        policy accept

        iifname $vmbr meta nfproto ipv6 counter drop
        iifname $vmbr ip saddr != $guest4 counter drop
        iifname $vmbr oifname != $uplink counter drop
        iifname $vmbr ip daddr @blocked4 counter drop
        iifname $vmbr ct state { new, established } tcp dport 443 counter accept
        iifname $vmbr counter drop

        oifname $vmbr meta nfproto ipv6 counter drop
        oifname $vmbr ct state established,related counter accept
        oifname $vmbr counter drop
    }
}
```

This table affects only traffic entering or leaving `virbr-agent`. Never add `flush ruleset`;
that would remove UFW, Docker, libvirt, and unrelated firewall state.

Syntax-check it:

```fish
sudo nft -c -f /etc/nftables.d/tenninety-agent.nft
```

### 9.4 Integrate with the host's existing UFW

The current target host uses UFW. Add only the traffic that remains allowed after the stricter
nftables table has dropped private destinations and other ports:

```fish
sudo ufw allow in on virbr-agent \
    proto udp from any port 68 to any port 67

sudo ufw allow in on virbr-agent \
    proto udp from 192.168.250.10 to 192.168.250.1 port 53

sudo ufw allow in on virbr-agent \
    proto tcp from 192.168.250.10 to 192.168.250.1 port 53

sudo ufw route allow \
    in on virbr-agent out on enp132s0 \
    proto tcp from 192.168.250.10 to any port 443

sudo ufw status verbose
```

If UFW is not active, do not enable it merely for these four rules; the dedicated nftables table
and libvirt rules remain the primary boundary.

### 9.5 Load the boundary automatically but keep the VM manual

Create `/etc/systemd/system/tenninety-agent-firewall.service`:

```ini
[Unit]
Description=Firewall boundary for the tenninety agent VM
After=ufw.service
Before=libvirtd.service
ConditionPathExists=/etc/nftables.d/tenninety-agent.nft

[Service]
Type=oneshot
ExecStartPre=-/usr/bin/nft delete table inet agent_vm_boundary
ExecStart=/usr/bin/nft -f /etc/nftables.d/tenninety-agent.nft
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
```

Enable and load it:

```fish
sudo systemctl daemon-reload
sudo systemctl enable --now tenninety-agent-firewall.service
sudo nft -a list table inet agent_vm_boundary
```

The service intentionally has no `ExecStop` that removes the boundary. Leave both `agent-net` and
the VM with autostart disabled. After every host reboot, firewall reload, VPN change, or uplink
change, verify this table before manually starting the network or VM.

---

## 10. Move the VM onto the secure network

### 10.1 Shut down the trusted base guest

Inside the VM:

```fish
sudo systemctl poweroff
```

On the physical host, verify it is off:

```fish
virsh -c qemu:///system domstate tenninety-agent
```

### 10.2 Replace the temporary NIC

Open virt-manager, select `tenninety-agent`, and open its hardware details while it is shut off.

1. Remove the NIC attached to `default`.
2. Add one **Virtual network** NIC using `agent-net`.
3. Select VirtIO as the device model.
4. Set its MAC address to `52:54:00:90:10:01`.
5. Confirm that exactly one NIC remains.

Verify that libvirt's installed anti-spoofing filter exists:

```fish
virsh -c qemu:///system nwfilter-dumpxml clean-traffic
```

If that command fails, stop and repair the libvirt installation. Then open the VM's XML editor in
virt-manager, or run `virsh -c qemu:///system edit tenninety-agent`, and add this block inside the
single `<interface type='network'>` element:

```xml
<filterref filter='clean-traffic'>
  <parameter name='IP' value='192.168.250.10'/>
</filterref>
```

This host-enforced filter pins the guest's MAC, ARP, and IPv4 source identity. The explicit IP still
permits the initial DHCP exchange from `0.0.0.0`.

Verify from the physical host:

```fish
virsh -c qemu:///system domiflist tenninety-agent
virsh -c qemu:///system dumpxml tenninety-agent
```

The interface source must be only `agent-net`, and it must contain the `clean-traffic` filter with
the fixed IP above. The XML must not contain a host filesystem, host device, USB redirector, or
SPICE agent channel.

Disable VM autostart:

```fish
virsh -c qemu:///system dom-autostart tenninety-agent --disable
```

### 10.3 Start in the safe order

```fish
sudo systemctl is-active tenninety-agent-firewall.service
sudo nft list table inet agent_vm_boundary

virsh -c qemu:///system net-start agent-net
virsh -c qemu:///system start tenninety-agent
```

If `agent-net` is already active, skip `net-start`.

Verify the reserved lease:

```fish
virsh -c qemu:///system net-dhcp-leases agent-net
```

The VM must receive `192.168.250.10`.

---

## 11. Harden the guest

Keep the virt-manager console open while applying guest firewall and SSH changes. If a rule is
wrong, the console remains available even when networking is blocked.

### 11.1 Disable guest IPv6 and forwarding

As `vmadmin`, create `/etc/sysctl.d/90-tenninety-agent.conf`:

```ini
net.ipv6.conf.all.disable_ipv6=1
net.ipv6.conf.default.disable_ipv6=1
net.ipv6.conf.all.forwarding=0
net.ipv4.ip_forward=0
```

Apply it:

```fish
sudo sysctl --system
```

### 11.2 Apply guest UFW defense in depth

Discover the guest interface first:

```fish
ip -brief link
ip -4 route
```

The examples assume `enp1s0`. Replace it if the guest reports another interface.

```fish
set GUEST_IF enp1s0

sudo ufw --force reset
sudo ufw default deny incoming
sudo ufw default deny outgoing
sudo ufw default deny routed

sudo ufw allow in on lo
sudo ufw allow out on lo

sudo ufw allow out on $GUEST_IF \
    proto udp from any port 68 to any port 67

sudo ufw allow in on $GUEST_IF \
    proto udp from 192.168.250.1 port 67 to any port 68

sudo ufw allow out on $GUEST_IF \
    proto udp to 192.168.250.1 port 53

sudo ufw allow out on $GUEST_IF \
    proto tcp to 192.168.250.1 port 53

sudo ufw allow out on $GUEST_IF \
    proto tcp to any port 443

sudo ufw allow in on $GUEST_IF \
    proto tcp from 192.168.250.1 to any port 22

sudo ufw --force enable
sudo systemctl enable ufw.service
sudo ufw status numbered
```

The host nftables table remains the authoritative boundary even if guest root disables UFW.

### 11.3 Restrict SSH behavior

As `vmadmin`, create `/etc/ssh/sshd_config.d/20-tenninety-agent.conf`:

```text
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
GatewayPorts no
AllowAgentForwarding no
X11Forwarding no

Match User runner
    AuthenticationMethods publickey
    AllowTcpForwarding no
    AllowStreamLocalForwarding no
    PermitTunnel no

Match User tunnel
    AuthenticationMethods publickey
    AllowTcpForwarding remote
    AllowStreamLocalForwarding no
    PermitListen 127.0.0.1:18080
    GatewayPorts no
    AllowAgentForwarding no
    X11Forwarding no
    MaxSessions 0
    PermitTTY no
    PermitTunnel no
    PermitUserRC no
```

Validate before restarting SSH:

```fish
sudo sshd -t
sudo systemctl restart sshd.service
```

### 11.4 Pin the guest SSH host key

From the trusted VM console, record the fingerprint:

```fish
sudo ssh-keygen -E sha256 -lf /etc/ssh/ssh_host_ed25519_key.pub
```

On the physical host:

```fish
ssh-keyscan -T 5 -t ed25519 192.168.250.10 \
    > ~/.ssh/known_hosts-tenninety-agent

ssh-keygen -E sha256 -lf ~/.ssh/known_hosts-tenninety-agent
chmod 0600 ~/.ssh/known_hosts-tenninety-agent
```

Compare the two fingerprints exactly before connecting.

### 11.5 Take the clean snapshot

Shut down the VM after hardening. In virt-manager, create a powered-off snapshot named:

```text
clean-agent-base
```

This snapshot must contain no project, Frontier key, llama-swap key, or generated code. Revert to
it before each new experiment.

After the powered-off snapshot completes, close the virt-manager viewer. Recheck and restart in the
safe order from a physical-host terminal:

```fish
sudo systemctl is-active tenninety-agent-firewall.service
sudo nft list table inet agent_vm_boundary
virsh -c qemu:///system net-info agent-net
```

If `net-info` reports `Active: no`, start the network first:

```fish
virsh -c qemu:///system net-start agent-net
```

Then start the VM and confirm its reserved lease:

```fish
virsh -c qemu:///system start tenninety-agent
virsh -c qemu:///system net-dhcp-leases agent-net
```

---

## 12. Create the narrow llama-swap tunnel

The tunnel is started by the physical host. The guest never initiates a connection to a host
network address.

### 12.1 Recheck both ends

On the physical host:

```fish
ss -ltn '( sport = :8080 )'
systemctl --user is-active llama-swap.service
```

The only listener must be `127.0.0.1:8080`.

### 12.2 Start the tunnel in its own host terminal

```fish
ssh -4 -N -T \
    -i ~/.ssh/tenninety-tunnel \
    -o IdentitiesOnly=yes \
    -o ForwardAgent=no \
    -o ForwardX11=no \
    -o ExitOnForwardFailure=yes \
    -o ServerAliveInterval=30 \
    -o ServerAliveCountMax=3 \
    -o StrictHostKeyChecking=yes \
    -o UserKnownHostsFile=~/.ssh/known_hosts-tenninety-agent \
    -R 127.0.0.1:18080:127.0.0.1:8080 \
    tunnel@192.168.250.10
```

Leave this terminal running. `Ctrl-C` closes the tunnel and immediately revokes the guest's model
access.

Do not use `-A`, `ForwardAgent=yes`, `-g`, or a remote bind of `0.0.0.0`.

### 12.3 Verify from the VM

Log in as runner from another physical-host terminal:

```fish
ssh -i ~/.ssh/tenninety-runner \
    -o IdentitiesOnly=yes \
    -o ForwardAgent=no \
    -o StrictHostKeyChecking=yes \
    -o UserKnownHostsFile=~/.ssh/known_hosts-tenninety-agent \
    runner@192.168.250.10
```

Inside the VM, enter the local key from the physical host's
`~/.config/llama-swap/secrets.env` without putting it in shell history:

```fish
set -gx PATH /usr/local/bin /usr/bin

read --silent --prompt-str 'llama-swap key: ' local_key
set -gx TENNINETY_LOCAL_API_KEY "$local_key"
set -e local_key

curl --fail --silent \
    -H "Authorization: Bearer $TENNINETY_LOCAL_API_KEY" \
    http://127.0.0.1:18080/v1/models | jq .
```

Both `qwen-coder` and `devstral-reviewer` must be listed.

---

## 13. Verify isolation before giving an agent work

### 13.1 Physical-host checks

```fish
virsh -c qemu:///system domiflist tenninety-agent
virsh -c qemu:///system dumpxml tenninety-agent
virsh -c qemu:///system net-dumpxml agent-net
virsh -c qemu:///system nwfilter-dumpxml clean-traffic
sudo nft -a list table inet agent_vm_boundary
sudo ufw status verbose
ss -ltn '( sport = :8080 )'
```

Required results:

- Exactly one VM NIC, attached only to `agent-net`.
- The NIC references `clean-traffic` with `IP=192.168.250.10`.
- No IPv6 `<ip>` element in `agent-net`.
- The nftables boundary is loaded.
- llama-swap listens only on physical-host loopback.
- The VM and `agent-net` are not configured for autostart.

### 13.2 Guest checks

Run inside the VM as runner:

```fish
sudo -n true                         # must fail
not test -e /home/operator          # host home must not exist
findmnt -t virtiofs,9p              # must show no mounts
ip -6 route show default            # must show no default route
date --utc                           # must show the current date and time

nc -4 -vz -w 3 192.168.250.1 22     # must fail
nc -4 -vz -w 3 192.168.250.1 8080   # direct host model access must fail
nc -4 -vz -w 3 192.168.50.1 53      # physical LAN must fail
curl --connect-timeout 3 http://1.1.1.1/  # public HTTP/80 must fail

curl --fail --silent https://api.nuget.org/v3/index.json > /dev/null
curl --fail --silent https://github.com/ > /dev/null

curl --fail --silent \
    -H "Authorization: Bearer $TENNINETY_LOCAL_API_KEY" \
    http://127.0.0.1:18080/v1/models > /dev/null
```

The blocked tests must fail while public HTTPS and the loopback tunnel succeed. A powered-off
snapshot boot should initialize from the host-backed virtual clock; stop before entering provider
keys if the guest date is wrong. Check the host nftables counters after blocked attempts to confirm
the firewall, rather than an inactive target, caused the failure.

TCP destination port 443 does not prove that traffic is HTTPS. The guest can still send
guest-visible data to an arbitrary public service on port 443. Use a controlled egress proxy if
domain-level allowlisting is required.

---

## 14. Initialize IncidentDesk inside the VM

### 14.1 Initialize before transferring the specification

Inside the VM as runner:

```fish
set -gx PATH /usr/local/bin /usr/bin

mkdir -p ~/workspaces/IncidentDesk
cd ~/workspaces/IncidentDesk
tenninety init
```

Running `init` first ensures the starter `spec.md` is already tracked.

### 14.2 Transfer only `spec.md` from the physical host

Run on the physical host:

```fish
scp \
    -i ~/.ssh/tenninety-runner \
    -o IdentitiesOnly=yes \
    -o ForwardAgent=no \
    -o StrictHostKeyChecking=yes \
    -o UserKnownHostsFile=~/.ssh/known_hosts-tenninety-agent \
    /home/operator/IncidentDesk/spec.md \
    runner@192.168.250.10:/home/runner/workspaces/IncidentDesk/spec.md
```

No host directory is mounted. This is a one-file transfer initiated by the physical host.

Inside the VM:

```fish
cd ~/workspaces/IncidentDesk
git diff -- spec.md
```

---

## 15. Configure isolated live mode

Inside the VM, edit `~/workspaces/IncidentDesk/.tenninety/config.json`. These values are the
important live-mode fields; omitted settings retain their initialized defaults.

```jsonc
{
  "provider_mode": "aider",
  "coder_agent": "aider",

  "frontier_endpoint": "https://YOUR-FRONTIER-PROVIDER/v1",
  "frontier_model": "your-frontier-model-name",
  "frontier_api_key_env": "TENNINETY_FRONTIER_API_KEY",

  "local_models": {
    "coder": "qwen-coder",
    "reviewer": "devstral-reviewer",
    "coder_endpoint": "",
    "reviewer_endpoint": ""
  },

  "use_llama_swap": true,
  "llama_swap_endpoint": "http://127.0.0.1:18080/v1",

  "sandbox": {
    "mode": "unsafe-host"
  },

  "aider": {
    "model": "openai/qwen-coder",
    "extra_args": "--no-auto-commits --no-gitignore --yes-always --no-check-update --no-analytics --disable-playwright --no-detect-urls --config /etc/tenninety/aider.conf.yml --env-file /dev/null"
  }
}
```

Rules:

- Replace the Frontier endpoint and model with real provider values.
- `qwen-coder` and `devstral-reviewer` must exactly match the host llama-swap YAML keys.
- The two aliases must continue to resolve to different GGUF files.
- While `use_llama_swap=true`, the shared and per-role local endpoint fields are ignored.
- The isolated path uses aider. OpenCode and Pi require their own provider transport setup.
- This guide deliberately selects `sandbox.mode=unsafe-host` **inside the disposable KVM guest**
  because its reverse-tunnel endpoint is guest loopback and the VM is the outer execution
  boundary. For Tenninety's Docker boundary instead, follow `SANDBOX-CONFIG.example.jsonc`, make
  the model endpoint reachable on the internal model network, and keep `mode=docker`.
- The aider flags suppress analytics, browser installation, URL detection, and project-local aider
  config or `.env` loading. The framework supplies the local model key directly to each invocation.
- Aider may warn that `openai/qwen-coder` has unknown context-window and cost metadata. This is
  expected for the local llama-swap alias; the llama-server `--ctx-size` remains the actual limit.

Commit the config by itself. Leave the changed `spec.md` for `tenninety plan` to accept and commit
with the generated plan:

```fish
cd ~/workspaces/IncidentDesk
git add .tenninety/config.json
git commit -m "configure isolated live mode"
```

### 15.1 Enter the Frontier key for this session

Use a dedicated spending-limited key. Do not place it in the project, VM template, cloud-init,
shell history, or shell startup files.

```fish
read --silent --prompt-str 'Frontier API key: ' frontier_key
set -gx TENNINETY_FRONTIER_API_KEY "$frontier_key"
set -e frontier_key
```

The framework's subprocess environment allowlist does not normally copy the Frontier key into
coder or test child environments. This is not a credential boundary: the orchestrator and its
children share the runner UID, so a hostile process may be able to inspect or interfere with the
parent through `/proc`, signals, or another same-user mechanism. Assume the Frontier key can be
compromised, and keep it dedicated, revocable, spending-limited, and short-lived.

---

## 16. Plan and run IncidentDesk

### 16.1 Final preflight

Inside the VM as runner:

```fish
cd ~/workspaces/IncidentDesk

set -gx PATH /usr/local/bin /usr/bin

git status --short
aider --version
dotnet --version

curl --fail --silent \
    -H "Authorization: Bearer $TENNINETY_LOCAL_API_KEY" \
    http://127.0.0.1:18080/v1/models | jq -r '.data[].id'
```

The only expected worktree change before planning is `spec.md`. The model list must contain both
configured identifiers.

### 16.2 Generate and inspect the plan

```fish
tenninety plan --spec ./spec.md
tenninety status
```

Do not use `--yes` on the first live run. Review the architecture map, assumptions, work-package
scope, dependencies, and acceptance criteria before accepting.

### 16.3 Start supervised execution

```fish
tenninety start
```

Dashboard keys:

| Key | Action |
| --- | --- |
| `P` | pause or resume |
| `S` | snapshot and pivot |
| `R` | mechanically revert a tested promotion |
| `L` | inspect audit tail |
| `Q` | quit the supervisor |

The first request for each role can take time because llama-swap unloads one model and loads the
other. If the SSH tunnel dies, model calls fail; restart and verify the tunnel before resuming.

---

## 17. Export results and reset the VM

Generated files and repositories are untrusted until reviewed. Do not execute generated binaries
on the physical host.

### 17.1 Completed project: export Git history

With tenninety stopped or completed, run inside the workspace in the still-running VM:

```fish
cd ~/workspaces/IncidentDesk
git status --short
git bundle create ~/IncidentDesk.bundle --all
git bundle verify ~/IncidentDesk.bundle
cd ~
sha256sum IncidentDesk.bundle > IncidentDesk.bundle.sha256
```

If `git status --short` prints anything, do not treat the bundle as a complete export. Commit the
intended source or use the resumable export instead.

On the physical host, choose a new empty quarantine destination and copy the two files explicitly:

```fish
install -d -m 0700 ~/Quarantine
mkdir -m 0700 ~/Quarantine/IncidentDesk

scp \
    -i ~/.ssh/tenninety-runner \
    -o IdentitiesOnly=yes \
    -o ForwardAgent=no \
    -o StrictHostKeyChecking=yes \
    -o UserKnownHostsFile=~/.ssh/known_hosts-tenninety-agent \
    runner@192.168.250.10:/home/runner/IncidentDesk.bundle \
    runner@192.168.250.10:/home/runner/IncidentDesk.bundle.sha256 \
    ~/Quarantine/IncidentDesk/

cd ~/Quarantine/IncidentDesk
sha256sum --check IncidentDesk.bundle.sha256
git init --bare IncidentDesk.verify.git
git -C IncidentDesk.verify.git bundle verify ../IncidentDesk.bundle
```

`git bundle verify` requires a repository context; the empty bare repository supplies one without
checking generated files out onto the physical host.

A Git bundle contains Git history, not ignored runtime files such as `.tenninety/state.json` and
`.tenninety/audit-log.jsonl`.

### 17.2 Paused project: export resumable state

First pause/stop tenninety and ensure no process is writing the workspace. Then copy to a dedicated
quarantine directory with safe-link handling:

```fish
install -d -m 0700 ~/Quarantine
mkdir -m 0700 ~/Quarantine/IncidentDesk-resumable

rsync --archive --safe-links \
    -e 'ssh -i ~/.ssh/tenninety-runner -o IdentitiesOnly=yes -o ForwardAgent=no -o StrictHostKeyChecking=yes -o UserKnownHostsFile=~/.ssh/known_hosts-tenninety-agent' \
    runner@192.168.250.10:/home/runner/workspaces/IncidentDesk/ \
    ~/Quarantine/IncidentDesk-resumable/
```

Review the export as data. Transfer it into another clean VM for execution; do not resume it on the
physical host.

### 17.3 Revoke and reset

1. Stop tenninety at a safe boundary.
2. Close the host SSH tunnel with `Ctrl-C`.
3. Remove the Frontier and local keys from the runner session or power off the VM.
4. Shut down the VM.
5. Verify exported checksums and Git bundle before discarding guest state.
6. Stop the idle network with `virsh -c qemu:///system net-destroy agent-net` on the host.
7. Revert the powered-off VM to the `clean-agent-base` snapshot in virt-manager.
8. Before the next experiment, repeat the safe start order and all Section 13 isolation checks.

Do not use broad storage-deletion commands such as `virsh undefine --remove-all-storage` without
first inspecting the domain's backing chain. Reverting the known clean snapshot is safer for this
single-VM workflow.

---

## 18. Direct-host live mode (not isolated)

Use this only if you deliberately accept that coder and test processes can access everything your
physical-host user can access.

Install aider on the physical host:

```fish
uv tool install aider-chat
install -d -m 0700 ~/.config/tenninety
printf '{}\n' > ~/.config/tenninety/aider.conf.yml
chmod 0600 ~/.config/tenninety/aider.conf.yml
```

Use host port 8080 instead of the VM tunnel:

```jsonc
{
  "provider_mode": "aider",
  "coder_agent": "aider",
  "use_llama_swap": true,
  "llama_swap_endpoint": "http://127.0.0.1:8080/v1",
  "sandbox": { "mode": "unsafe-host" },
  "local_models": {
    "coder": "qwen-coder",
    "reviewer": "devstral-reviewer"
  },
  "aider": {
    "model": "openai/qwen-coder",
    "extra_args": "--no-auto-commits --no-gitignore --yes-always --no-check-update --no-analytics --disable-playwright --no-detect-urls --config /home/operator/.config/tenninety/aider.conf.yml --env-file /dev/null"
  }
}
```

This explicit `unsafe-host` setup is not equivalent to the KVM or Docker paths. A separate folder,
environment allowlist, or Git branch is not a filesystem sandbox.

---

## 19. Alternative vLLM topology

The repository's `docker-compose.yml` serves coder and reviewer models on physical-host loopback
ports 8000 and 8001. The defaults expect two NVIDIA GPUs and are not suitable for two large models
on one 24 GB card.

From inside the VM, physical-host `127.0.0.1:8000` and `:8001` are not reachable directly. Create
separate restricted reverse forwards to guest loopback ports or place model services behind one
authenticated proxy. Never bind them to `0.0.0.0` merely to make the VM connection work.

---

## 20. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `/dev/kvm` is missing | virtualization disabled in firmware or KVM unavailable | enable Intel VT-x/AMD-V; reboot; rerun `virt-host-validate` |
| virt-manager asks for sudo or uses session mode | wrong libvirt connection | use `virt-manager --connect qemu:///system`; never run the GUI with sudo |
| `network 'default' is not active` during installation | temporary provisioning network stopped | run `virsh -c qemu:///system net-start default` |
| `agent-net` cannot start | XML, dnsmasq, bridge, or firewall conflict | inspect `journalctl -u libvirtd`, `virsh net-dumpxml agent-net`, and existing routes |
| VM receives a different address | NIC MAC does not match reservation | set exactly `52:54:00:90:10:01`; remove extra NICs |
| VM fails to start after adding the filter | `clean-traffic` is absent or its XML is wrong | run `virsh nwfilter-dumpxml clean-traffic`; repair the filter before weakening or removing it |
| Guest has no DNS | UFW or host input rule blocks port 53 | inspect UFW rules and nft counters; verify guest DNS is `192.168.250.1` |
| Public HTTPS fails | uplink changed or UFW route rule missing | rerun `ip -4 route get 1.1.1.1`; update both network XML and firewall deliberately |
| HTTPS certificates fail after snapshot restore | guest clock is stale | compare `date --utc` with the host; power off and cold-start from the powered-off snapshot rather than opening broad NTP access |
| Guest can reach physical LAN | firewall table missing, wrong bridge, or extra NIC | stop VM immediately; inspect `domiflist`, nft table, and routes |
| SSH runner login fails | wrong key, host-key file, guest UFW, or sshd config | use virt-manager console; run `sshd -t`; compare fingerprints |
| Remote forwarding is rejected | tunnel Match block or key restriction is wrong | verify `AllowTcpForwarding remote`, `PermitListen`, authorized-key prefix, and `GatewayPorts no` |
| Guest port 18080 is already used | stale tunnel or local listener | run `ss -ltn '( sport = :18080 )'`; stop the stale process before retrying |
| Host 8080 works but guest 18080 fails | reverse tunnel absent or wrong destination | restart foreground SSH tunnel with `ExitOnForwardFailure`; then test from guest |
| `/v1/models` returns 401 | local API key mismatch | make `TENNINETY_LOCAL_API_KEY` match host `LLAMA_SWAP_API_KEY` |
| Model identifier startup abort | coder and reviewer names match | use distinct aliases and verify they point to different GGUF checksums |
| Aider repeatedly exits | aider missing, model name wrong, tunnel down, or endpoint unavailable | run `aider --version`; query `/v1/models`; inspect tunnel and llama-swap logs |
| Model OOM or very slow load | context/quant too large | lower context, choose Q3/Q4 quant, and inspect `journalctl --user -u llama-swap` |
| Frontier planning fails | endpoint/key/network incorrect | verify public HTTPS, provider URL, model ID, and session key |
| `Working tree is not clean` | uncommitted config or unrelated edits | inspect `git status`; commit only intended configuration before starting |
| Queue deadlocks with exit 4 | BLOCKED work package gates dependents | fix root cause or pivot REWORK, then resume and start |
| Git bundle misses runtime state | runtime files are intentionally ignored | use the stopped-workspace rsync procedure for resumability |
| `git bundle verify` says a repository is required | verification ran in the quarantine directory itself | initialize the empty bare verification repository and run the documented `git -C` command |
| Snapshot contains keys/project | snapshot taken too late | delete that snapshot, clean/rebuild the base, and snapshot before injecting secrets |

Useful diagnostics:

```fish
# Physical host
systemctl --user status llama-swap.service
journalctl --user -u llama-swap.service
sudo systemctl status tenninety-agent-firewall.service
sudo nft -a list table inet agent_vm_boundary
virsh -c qemu:///system net-list --all
virsh -c qemu:///system net-dhcp-leases agent-net
virsh -c qemu:///system domiflist tenninety-agent

# Guest
sudo ufw status numbered
ip -4 route
ip -6 route
ss -ltn
tenninety status
```

---

## 21. Security limitations

- A KVM escape is unlikely but possible. Keep the host kernel, CPU microcode, QEMU, libvirt,
  OpenSSH, llama-swap, and llama.cpp updated.
- Public TCP port 443 remains available from the guest. This supports Frontier, NuGet, and GitHub,
  but it also permits exfiltration of data visible inside the guest. Use an allowlisted egress proxy
  for stricter control.
- The reverse tunnel intentionally gives the guest access to llama-swap's authenticated API. An
  agent can consume GPU time and call any llama-swap endpoint authorized by that key, including
  model-management endpoints. Use a dedicated llama-swap instance and keep request capture off.
- llama-swap and llama.cpp run as the physical-host user in this guide and parse requests controlled
  by the guest. A vulnerability in either process could expose that host account; a dedicated
  least-privilege model-service account or similarly constrained service is stronger.
- DHCP and DNS deliberately expose narrow libvirt/dnsmasq services to the guest. Keep libvirt and
  dnsmasq patched and do not add other host services to the input allowlist.
- The host SSH client parses traffic from an untrusted guest SSH server. Keep OpenSSH patched, pin
  the guest host key, and never forward the host SSH agent.
- The Frontier key exists in the guest orchestrator process during live execution. Same-UID child
  isolation is not guaranteed, even though normal environment inheritance is filtered. Use a
  dedicated, revocable, spending-limited key.
- Snapshot deletion is not guaranteed secure erasure on SSD storage. Use encrypted host storage if
  guest data is sensitive.
- Exported source can contain malicious build scripts, symlinks, or generated binaries. Inspect and
  rebuild it in a separate clean VM before trusting it.
- Do not run the physical-host user as the agent, do not give runner sudo, and do not weaken the
  boundary for convenience after verification.

---

## 22. References

- [10/90 overview](OVERVIEW.md)
- [Specification authoring](SPEC-AUTHORING.md)
- [Junior guide](JUNIOR-GUIDE.md)
- [Senior guide](SENIOR-GUIDE.md)
- [ArchWiki KVM](https://wiki.archlinux.org/title/KVM)
- [ArchWiki libvirt](https://wiki.archlinux.org/title/Libvirt)
- [ArchWiki virt-manager](https://wiki.archlinux.org/title/Virt-manager)
- [libvirt network XML](https://libvirt.org/formatnetwork.html)
- [llama-swap configuration](https://github.com/mostlygeek/llama-swap/blob/main/docs/configuration.md)
- [OpenSSH port forwarding](https://man.openbsd.org/ssh)
