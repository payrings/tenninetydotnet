#!/usr/bin/env python3
# Copyright 2026 G. Paganelli
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
"""Package the current diff + a test failure log and get a frontier triage
pass via OpenRouter – with a hard one-shot escalation policy.

Routed through OpenRouter so FRONTIER_MODEL env var picks the actual model.
"""
import json
import hashlib
import fcntl
import os
import re
import subprocess
import sys
from pathlib import Path

FRONTIER_MODEL = os.environ.get("FRONTIER_MODEL", "z-ai/glm-5.2:floor")


def runtime_dir():
    """Return the same external, per-workspace state directory as dev.sh."""
    workspace = Path.cwd().resolve()
    configured = os.environ.get("TENNINETY_RUNTIME_DIR")
    if configured:
        directory = Path(configured).expanduser().resolve()
    else:
        state_home = os.environ.get("TENNINETY_STATE_HOME")
        if state_home:
            base = Path(state_home).expanduser()
        else:
            xdg_state = os.environ.get("XDG_STATE_HOME")
            base = Path(xdg_state).expanduser() if xdg_state else Path.home() / ".local" / "state"
            base /= "tenninetydotnet"
        workspace_id = hashlib.sha256(str(workspace).encode()).hexdigest()[:24]
        directory = (base / workspace_id).resolve()
    if directory == workspace or workspace in directory.parents:
        raise RuntimeError("TENNINETY_RUNTIME_DIR must be outside the workspace")
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    directory.chmod(0o700)
    directory = directory.resolve()
    if directory == workspace or workspace in directory.parents:
        raise RuntimeError("resolved TENNINETY_RUNTIME_DIR must be outside the workspace")
    os.environ["TENNINETY_RUNTIME_DIR"] = str(directory)
    return directory


ESCALATION_LOG = runtime_dir() / "escalations.json"
ESCALATION_LOCK = runtime_dir() / "escalations.lock"


def load_env_file(path=None):
    """Load KEY=VALUE lines from a permission-restricted .env into os.environ
    (without overwriting values already set in the environment).

    Storing the OpenRouter key in a fish universal variable writes it in
    plaintext to ~/.config/fish and exports it to every process the shell
    spawns. A mode-600 .env read only by this tool is a tighter blast radius.
    Search order: an explicit path, $TENNINETY_ENV, then
    ~/.config/tenninety/.env. A project-local .env is deliberately never read:
    workspace content must not be able to select or shadow cloud credentials.
    """
    candidates = []
    if path:
        candidates.append(Path(path))
    if os.environ.get("TENNINETY_ENV"):
        candidates.append(Path(os.environ["TENNINETY_ENV"]))
    candidates.append(Path.home() / ".config" / "tenninety" / ".env")

    for env_path in candidates:
        try:
            if not env_path.is_file():
                continue
        except OSError:
            continue
        # Warn if the file is group/world readable — it holds a secret.
        try:
            mode = env_path.stat().st_mode
            if mode & 0o077:
                print(f"WARNING: {env_path} is readable by group/other; "
                      f"run: chmod 600 {env_path}", file=sys.stderr)
        except OSError:
            pass
        for line in env_path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            # Do not override an explicitly-exported environment value.
            os.environ.setdefault(key, value)
        return env_path
    return None


def require_api_key():
    key = os.environ.get("OPENROUTER_API_KEY")
    if not key:
        print("ERROR: OPENROUTER_API_KEY is not set.", file=sys.stderr)
        print("Put it in a mode-600 .env (recommended):", file=sys.stderr)
        print("  mkdir -p ~/.config/tenninety && umask 077 && \\", file=sys.stderr)
        print("    printf 'OPENROUTER_API_KEY=sk-or-v1-...\\n' > ~/.config/tenninety/.env",
              file=sys.stderr)
        print("or export it in your shell for this session.", file=sys.stderr)
        sys.exit(1)
    return key


def load_counts():
    if not ESCALATION_LOG.exists():
        return {}
    try:
        counts = json.loads(ESCALATION_LOG.read_text())
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"cannot read {ESCALATION_LOG}: {exc}") from exc
    if not isinstance(counts, dict) or any(
        not isinstance(key, str) or not isinstance(value, int) or value < 0
        for key, value in counts.items()
    ):
        raise RuntimeError(f"{ESCALATION_LOG} must contain an object of non-negative integer counters")
    return counts


def save_counts(counts):
    temporary = ESCALATION_LOG.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(counts, indent=2) + "\n")
    temporary.chmod(0o600)
    temporary.replace(ESCALATION_LOG)


def git_text(args, *, accepted=(0,)):
    result = subprocess.run(["git", *args], capture_output=True, text=True)
    if result.returncode not in accepted:
        detail = result.stderr.strip() or result.stdout.strip() or f"exit {result.returncode}"
        raise RuntimeError(f"git {' '.join(args)} failed: {detail}")
    return result.stdout


def get_diff(feature, *, allow_head=False):
    """Diff since the active implementation/repair baseline."""
    baseline_file = runtime_dir() / feature / "active-baseline"
    active_baseline = None
    try:
        if baseline_file.is_file():
            active_baseline = baseline_file.read_text().splitlines()[0].strip()
    except (OSError, IndexError):
        active_baseline = None

    repair_tags = git_text([
        "tag", "--list", f"module-repair-{feature}-*", "--sort=-version:refname"
    ]).splitlines()
    rejection_commits = git_text([
        "log", "-n", "1", "--format=%H",
        f"--grep=^review({feature}): reject attempt [0-9][0-9]*$", "HEAD",
    ]).splitlines()
    refs = [ref for ref in (
        active_baseline,
        repair_tags[0] if repair_tags else None,
        rejection_commits[0] if rejection_commits else None,
        f"module-start-{feature}",
        "HEAD" if allow_head else None,
    ) if ref]
    for ref in refs:
        if subprocess.run(["git", "rev-parse", "--verify", "--quiet", ref],
                          capture_output=True).returncode == 0:
            diff = git_text([
                "diff", "--no-color", ref, "--", ".",
                ":(exclude)REVIEW_QUEUE.md",
                ":(exclude)review-feedback/**",
            ])
            untracked = git_text(["ls-files", "--others", "--exclude-standard"]).splitlines()
            for path in untracked:
                if path.startswith("review-feedback/"):
                    continue
                diff += git_text(["diff", "--no-index", "--no-color", "--", "/dev/null", path],
                                 accepted=(0, 1))
            return diff
    raise RuntimeError(
        f"no active baseline exists for '{feature}'; run dev.sh start first"
    )


def bounded_context(text, limit, label):
    """Bound external prompt payloads while retaining both diagnosis context
    and the latest failure output. The marker makes truncation explicit to the
    frontier model and to the human reviewing its response.
    """
    if len(text) <= limit:
        return text
    head = (limit * 2) // 3
    tail = limit - head
    omitted = len(text) - limit
    return (
        text[:head]
        + f"\n\n--- {label} TRUNCATED: {omitted} characters omitted ---\n\n"
        + text[-tail:]
    )


def main():
    if len(sys.argv) < 2:
        print("Usage: escalate.py <module-id> [test-log-file] [--override] [--write-code] [--dry-run]")
        sys.exit(1)

    feature = sys.argv[1]
    if not re.fullmatch(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*", feature):
        print(f"ERROR: invalid Module ID '{feature}' (expected lowercase kebab-case).", file=sys.stderr)
        sys.exit(1)
    test_log_path = next((a for a in sys.argv[2:] if not a.startswith("--")), None)
    allowed_flags = {"--override", "--write-code", "--dry-run"}
    unknown_flags = [a for a in sys.argv[2:] if a.startswith("--") and a not in allowed_flags]
    positional = [a for a in sys.argv[2:] if not a.startswith("--")]
    if unknown_flags or len(positional) > 1:
        print(f"ERROR: unrecognised or extra arguments: {' '.join(unknown_flags + positional[1:])}",
              file=sys.stderr)
        sys.exit(1)
    override = "--override" in sys.argv
    write_code = "--write-code" in sys.argv
    # --dry-run writes artefacts to a temp dir instead of the workspace and does
    # does not touch the external escalation counter, so you can exercise the tool without leaving
    # escalation-notes.md / frontier-fix-*.md behind for you to clean up.
    dry_run = "--dry-run" in sys.argv

    # Load the key from a mode-600 .env (preferred) before reading it.
    load_env_file()
    frontier_model = os.environ.get("FRONTIER_MODEL", FRONTIER_MODEL)

    # Serialize the read/check/call/write sequence. Atomic JSON replacement
    # alone does not stop two concurrent processes from consuming the same
    # escalation tier.
    lock_handle = ESCALATION_LOCK.open("a+")
    fcntl.flock(lock_handle.fileno(), fcntl.LOCK_EX)
    try:
        counts = load_counts()
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
    prior = counts.get(feature, 0)

    # Enforce the documented order, rather than merely checking for an override
    # flag. A first-call --override --write-code must not skip both plan tiers,
    # and repeated overrides must not create an unbounded escalation loop.
    if prior == 0 and (override or write_code):
        print("ERROR: tier 1 must be a plan-only call without --override or --write-code.")
        sys.exit(2)
    if prior == 1 and (not override or write_code):
        print("ERROR: tier 2 requires --override and remains plan-only.")
        sys.exit(2)
    if prior == 2 and (not override or not write_code):
        print("ERROR: tier 3 requires --override --write-code.")
        sys.exit(2)
    if prior >= 3:
        print(f"STOP: '{feature}' has completed all three escalation tiers.")
        print("Revise the specification or apply a direct human-authored fix.")
        sys.exit(2)

    try:
        from openai import OpenAI
    except ImportError:
        print("ERROR: Python package 'openai' is not installed; see SETUP_GUIDE.md Phase 8.",
              file=sys.stderr)
        sys.exit(1)

    client = OpenAI(
        base_url="https://openrouter.ai/api/v1",
        api_key=require_api_key(),
    )
    try:
        diff = get_diff(feature, allow_head=dry_run)
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
    test_log = ""
    if test_log_path:
        try:
            test_log = Path(test_log_path).read_text()
        except OSError as e:
            print(f"ERROR: could not read test log '{test_log_path}': {e}", file=sys.stderr)
            sys.exit(1)

    diff = bounded_context(diff, 120_000, "DIFF")
    test_log = bounded_context(test_log, 40_000, "TEST LOG")

    second_attempt_note = ""
    if prior >= 1:
        second_attempt_note = (
            "\n\nNote: a previous escalation already proposed a fix plan for "
            "this exact feature, and implementing that plan still failed. Do "
            "not simply propose another confident plan. If you don't have "
            "enough information to be confident about the real cause, say so "
            "and ask a clarifying question instead of guessing again."
        )

    if write_code:
        prompt = f"""You are fixing a stuck local coding agent that has failed twice.
The previous escalation gave a plan, and implementing that plan also failed.
Write the actual fix code now – not a plan, the code itself.

For each file that needs to change, output:
### File: <path>
```<language>
<full file content or the specific changes needed>
```

Be specific and complete. The human will apply this code directly.

## Diff (current state)
{diff}

## Test failure log
{test_log}
"""
        output_name = f"frontier-fix-{feature}.md"
        max_tokens = 4000
    else:
        prompt = f"""You are triaging a stuck local coding agent.
Diagnose the likely root cause of the diff and failure log below and propose
a concrete fix plan – a plan, not full code; a human or the local Coder model
will implement it.{second_attempt_note}

## Diff
{diff}

## Test failure log
{test_log}
"""
        output_name = "escalation-notes.md"
        max_tokens = 2000

    if dry_run:
        import tempfile
        out_dir = Path(tempfile.mkdtemp(prefix="tenninety-escalate-"))
        output_file = out_dir / output_name
    else:
        out_dir = runtime_dir() / feature
        out_dir.mkdir(parents=True, exist_ok=True)
        output_file = out_dir / output_name

    resp = client.chat.completions.create(
        model=frontier_model,
        max_tokens=max_tokens,
        messages=[{"role": "user", "content": prompt}],
    )

    if not resp.choices:
        print("ERROR: frontier model returned no choices; escalation counter was not changed.",
              file=sys.stderr)
        sys.exit(1)
    choice = resp.choices[0]
    if choice.finish_reason != "stop":
        print(f"ERROR: frontier response ended with finish_reason={choice.finish_reason!r}; "
              "incomplete output was not saved and the escalation counter was not changed.",
              file=sys.stderr)
        sys.exit(1)
    content = choice.message.content
    if not isinstance(content, str) or not content.strip():
        print("ERROR: frontier model returned no text; escalation counter was not changed.",
              file=sys.stderr)
        sys.exit(1)
    output_temporary = output_file.with_name(output_file.name + ".tmp")
    output_temporary.write_text(content)
    output_temporary.chmod(0o600)
    output_temporary.replace(output_file)

    # Increment the one-shot counter only after the artefact is safely written,
    # so a crash mid-write doesn't consume the escalation with nothing to show.
    if not dry_run:
        counts[feature] = prior + 1
        save_counts(counts)

    print(content)
    print(f"\n--- Saved to {output_file} ---")
    if dry_run:
        print("(dry run: wrote to a temp dir and did not update the escalation counter)")


if __name__ == "__main__":
    main()
