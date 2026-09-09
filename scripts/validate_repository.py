"""Validate tracked Markdown and JSON without third-party dependencies."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess
from urllib.parse import unquote, urlsplit


FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")
INLINE_CODE = re.compile(r"(`+).*?\1")
DESTINATION = r"(<[^>\n]*>|(?:\\.|[^\s()\\]|\([^()\n]*\))*)"
INLINE_LINK = re.compile(
    r"!?\[[^\]\n]*\]\(\s*" + DESTINATION
    + r"(?:\s+(?:\"[^\"\n]*\"|'[^'\n]*'|\([^()\n]*\)))?\s*\)"
)
REFERENCE_LINK = re.compile(r"^ {0,3}\[[^\]\n]+\]:\s*" + DESTINATION)


def markdown_links(content: str):
    """Yield line numbers and destinations from ordinary inline/reference links.

    This intentionally covers this repository's Markdown conventions, rather
    than implementing a full Markdown parser. Code fences and inline code are
    excluded; reference definitions are checked even when not yet used.
    """
    fence_character = ""
    fence_length = 0
    for line_number, line in enumerate(content.splitlines(), 1):
        fence = FENCE.match(line)
        if fence_character:
            if (fence and fence[1][0] == fence_character
                    and len(fence[1]) >= fence_length and not fence[2].strip()):
                fence_character = ""
            continue
        if fence:
            fence_character, fence_length = fence[1][0], len(fence[1])
            continue
        line = INLINE_CODE.sub("", line)
        reference = REFERENCE_LINK.match(line)
        if reference:
            yield line_number, reference[1]
        for match in INLINE_LINK.finditer(line):
            yield line_number, match[1]


def local_target(root: Path, document: Path, destination: str) -> Path | None:
    destination = destination.removeprefix("<").removesuffix(">")
    destination = re.sub(r"\\([\\() ])", r"\1", destination)
    url = urlsplit(destination)
    if url.scheme or url.netloc or not url.path:
        return None
    path = unquote(url.path)
    return root / path.lstrip("/") if path.startswith("/") else document.parent / path


def reject_json_constant(value: str):
    raise ValueError(f"{value} is not a JSON value")


def validate_repository(root: Path) -> tuple[int, list[str]]:
    """Return the checked file count and errors; only tracked sources are read."""
    result = subprocess.run(
        ["git", "-C", str(root), "ls-files", "--cached", "-z"],
        check=True, capture_output=True,
    )
    tracked = result.stdout.decode("utf-8").split("\0")
    errors = []
    checked = 0
    for name in tracked:
        path = root / name
        if path.suffix.lower() not in {".md", ".markdown", ".json"}:
            continue
        checked += 1
        try:
            raw = path.read_bytes()
            content = raw.decode("utf-8", errors="strict")
        except (OSError, UnicodeError) as error:
            errors.append(f"{name}: cannot read strict UTF-8: {error}")
            continue
        if path.suffix.lower() == ".json":
            try:
                json.loads(content, parse_constant=reject_json_constant)
            except ValueError as error:
                errors.append(f"{name}: invalid JSON: {error}")
            continue
        if b"\r" in raw:
            errors.append(f"{name}: use LF line endings (found CR)")
        if "\ufffd" in content:
            errors.append(f"{name}: contains replacement character U+FFFD")
        for line_number, destination in markdown_links(content):
            try:
                target = local_target(root, path, destination)
                if target is not None and not target.exists():
                    errors.append(f"{name}:{line_number}: missing local link: {destination}")
            except (OSError, ValueError) as error:
                errors.append(f"{name}:{line_number}: invalid link {destination}: {error}")
    return checked, errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root", type=Path, default=Path(__file__).resolve().parents[1],
        help="repository root (defaults to this script's repository)",
    )
    arguments = parser.parse_args()
    try:
        checked, errors = validate_repository(arguments.root.resolve())
    except (OSError, UnicodeError, subprocess.CalledProcessError) as error:
        print(f"Repository validation failed: {error}")
        return 1
    if errors:
        for error in errors:
            print(error)
        print(f"Failed: {len(errors)} problem(s) in {checked} tracked Markdown/JSON file(s).")
        return 1
    print(f"Passed: {checked} tracked Markdown/JSON file(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
