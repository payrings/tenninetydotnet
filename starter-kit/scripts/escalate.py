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
import os
import subprocess
import sys
from pathlib import Path

from openai import OpenAI

ESCALATION_LOG = Path(".escalations.json")
FRONTIER_MODEL = os.environ.get("FRONTIER_MODEL", "anthropic/claude-opus-4.8")


def load_env_file(path=None):
    """Load KEY=VALUE lines from a permission-restricted .env into os.environ
    (without overwriting values already set in the environment).

    Storing the OpenRouter key in a fish universal variable writes it in
    plaintext to ~/.config/fish and exports it to every process the shell
    spawns. A mode-600 .env read only by this tool is a tighter blast radius.
    Search order: $TENNINETY_ENV, ./.env, ~/.config/tenninety/.env.
    """
    candidates = []
    if path:
        candidates.append(Path(path))
    if os.environ.get("TENNINETY_ENV"):
        candidates.append(Path(os.environ["TENNINETY_ENV"]))
    candidates.append(Path(".env"))
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
    return json.loads(ESCALATION_LOG.read_text()) if ESCALATION_LOG.exists() else {}


def save_counts(counts):
    ESCALATION_LOG.write_text(json.dumps(counts, indent=2))


def get_diff(feature):
    """Diff since the module's starting point, not just uncommitted work."""
    for ref in (f"module-start-{feature}", "HEAD"):
        if subprocess.run(["git", "rev-parse", "--verify", "--quiet", ref],
                          capture_output=True).returncode == 0:
            return subprocess.run(["git", "diff", ref],
                                  capture_output=True, text=True).stdout
    return subprocess.run(["git", "diff"], capture_output=True, text=True).stdout


def main():
    if len(sys.argv) < 2:
        print("Usage: escalate.py <module-id> [test-log-file] [--override] [--write-code] [--dry-run]")
        sys.exit(1)

    feature = sys.argv[1]
    test_log_path = next((a for a in sys.argv[2:] if not a.startswith("--")), None)
    override = "--override" in sys.argv
    write_code = "--write-code" in sys.argv
    # --dry-run writes artefacts to a temp dir instead of the workspace and does
    # not touch .escalations.json, so you can exercise the tool without leaving
    # escalation-notes.md / frontier-fix-*.md behind for you to clean up.
    dry_run = "--dry-run" in sys.argv

    if write_code and not override:
        print("ERROR: --write-code requires --override (it's a second-escalation tier).")
        print("The first escalation produces a plan. If that plan fails, you can")
        print("explicitly request the frontier to write code with --override --write-code.")
        sys.exit(1)

    # Load the key from a mode-600 .env (preferred) before reading it.
    load_env_file()
    frontier_model = os.environ.get("FRONTIER_MODEL", FRONTIER_MODEL)

    counts = load_counts()
    prior = counts.get(feature, 0)

    if prior >= 1 and not override:
        print(f"""
STOP: '{feature}' was already escalated once, and the frontier-guided fix
that came out of it still failed review or testing.

This is not a signal to try again automatically. Read escalation-notes.md,
the latest failure log, and the diff yourself, and decide whether the
specification is wrong, the frontier's plan was wrong, or this needs a
direct manual fix. That decision needs a person, not another AI pass.

If you've genuinely reviewed this and still want one more AI-assisted
attempt, re-run with --override. That flag exists to require a conscious
action, not a habit.

If you want the frontier model to write the actual fix code (not just a
plan), re-run with --override --write-code. The output will be saved to
frontier-fix-{feature}.md for you to apply.
""")
        sys.exit(2)

    client = OpenAI(
        base_url="https://openrouter.ai/api/v1",
        api_key=require_api_key(),
    )
    diff = get_diff(feature)
    test_log = ""
    if test_log_path:
        try:
            test_log = Path(test_log_path).read_text()
        except OSError as e:
            print(f"ERROR: could not read test log '{test_log_path}': {e}", file=sys.stderr)
            sys.exit(1)

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
        output_file = Path(output_name)

    resp = client.chat.completions.create(
        model=frontier_model,
        max_tokens=max_tokens,
        messages=[{"role": "user", "content": prompt}],
    )

    content = resp.choices[0].message.content
    output_file.write_text(content)

    # Increment the one-shot counter only after the artefact is safely written,
    # so a crash mid-write doesn't consume the escalation with nothing to show.
    if not dry_run:
        counts[feature] = prior + 1
        save_counts(counts)

    print(content)
    print(f"\n--- Saved to {output_file} ---")
    if dry_run:
        print("(dry run: wrote to a temp dir and did not update .escalations.json)")


if __name__ == "__main__":
    main()
