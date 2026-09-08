#!/usr/bin/env python3
"""Static configuration checks for continuous verification.

1. JSON / JSONC / YAML / XML configuration syntax for every tracked configuration file
   (build output, .git internals and vendored folders are skipped);
2. broken local Markdown links across README.md, docs/, models/ and samples/ (relative
   targets only; absolute URLs, anchors and mailto links are ignored).

Exits nonzero and reports every finding when a check fails.
"""

import glob
import json
import os
import re
import sys

failures = 0

SKIP_DIRECTORY_PARTS = {"bin", "obj", "node_modules", ".git"}


def bad(message: str) -> None:
    global failures
    failures += 1
    print("ERROR: " + message, file=sys.stderr)


def parse_jsonc(text: str) -> None:
    """Parses JSONC (// and /* */ comments plus trailing commas) like the annotated example."""
    out: list[str] = []
    i, n = 0, len(text)
    in_string = False
    while i < n:
        c = text[i]
        if in_string:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_string = False
            i += 1
            continue
        if c == '"':
            in_string = True
            out.append(c)
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
            continue
        out.append(c)
        i += 1
    cleaned = re.sub(r",\s*([}\]])", r"\1", "".join(out))
    json.loads(cleaned)


def parse_yaml(text: str) -> None:
    try:
        import yaml
    except ImportError:
        import subprocess

        subprocess.check_call([sys.executable, "-m", "pip", "install", "--quiet", "pyyaml"])
        import yaml
    list(yaml.safe_load_all(text))


def parse_xml(text: str) -> None:
    import xml.etree.ElementTree as ET

    ET.fromstring(text)


def configuration_files() -> list[str]:
    found: list[str] = []
    for pattern in ("*.json", "*.jsonc", "*.yml", "*.yaml", "*.xml"):
        for path in glob.glob(os.path.join("**", pattern), recursive=True):
            if any(part in SKIP_DIRECTORY_PARTS for part in path.split(os.sep)):
                continue
            found.append(path)
    return sorted(set(found))


def check_configurations() -> None:
    for path in configuration_files():
        try:
            with open(path, encoding="utf-8") as handle:
                text = handle.read()
            if path.endswith(".jsonc"):
                parse_jsonc(text)
            elif path.endswith(".json"):
                json.loads(text)
            elif path.endswith((".yml", ".yaml")):
                parse_yaml(text)
            else:
                parse_xml(text)
            print("ok: " + path)
        except Exception as error:  # noqa: BLE001 - report every syntax failure by name
            bad(f"{path}: {type(error).__name__}: {error}")


def check_markdown_links() -> None:
    link_re = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
    markdowns = ["README.md"] + sorted(
        glob.glob("docs/*.md") + glob.glob("models/*.md") + glob.glob("samples/*.md")
    )
    for path in markdowns:
        if not os.path.exists(path):
            continue
        base = os.path.dirname(path)
        with open(path, encoding="utf-8") as handle:
            for lineno, line in enumerate(handle, start=1):
                for target in link_re.findall(line):
                    if target.startswith(("http://", "https://", "mailto:", "#")):
                        continue
                    clean = target.split("#", 1)[0]
                    if not clean:
                        continue
                    candidate = os.path.normpath(os.path.join(base, clean))
                    if not os.path.exists(candidate):
                        bad(f"{path}:{lineno}: broken local link '{target}'")


def main() -> int:
    check_configurations()
    check_markdown_links()
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
