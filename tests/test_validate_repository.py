"""Exercise repository checks against small, real Git indexes."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from validate_repository import validate_repository


class RepositoryValidationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.git("init", "--quiet")

    def git(self, *arguments):
        subprocess.run(
            ["git", "-C", str(self.root), *arguments],
            check=True, capture_output=True,
        )

    def write(self, name, content, *, tracked=True):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content.encode("utf-8") if isinstance(content, str) else content)
        if tracked:
            self.git("add", "--", name)

    def test_valid_links_spaces_fragments_and_code_examples(self):
        self.write("docs/guide one.md", "# Guide\n")
        self.write("settings.json", '{"enabled": true}\n')
        self.write("README.md", """# Links
[angle](<docs/guide one.md>)
[encoded](docs/guide%20one.md#guide "title")
[reference][guide]
[guide]: <docs/guide one.md> "Guide"
[anchor](#links) [web](https://example.com/missing) [mail](mailto:test@example.com)
`[example](missing-inline.md)`
```markdown
[example](missing-fenced.md)
[example]: missing-reference.md
```
~~~~
[example](missing-tilde.md)
~~~~
""")
        self.assertEqual(validate_repository(self.root), (3, []))

    def test_broken_inline_and_reference_links_report_document_and_line(self):
        self.write("docs/source.md", "[bad](missing.md)\n[ref]: ../absent.json\n")
        checked, errors = validate_repository(self.root)
        self.assertEqual(checked, 1)
        self.assertEqual(len(errors), 2)
        self.assertIn("docs/source.md:1: missing local link: missing.md", errors)
        self.assertIn("docs/source.md:2: missing local link: ../absent.json", errors)

    def test_markdown_encoding_and_line_ending_failures(self):
        self.write("bad-utf8.md", b"\xff\n")
        self.write("bad-newline.md", b"# Title\r\n")
        self.write("bad-text.md", "Replacement: \ufffd\n")
        checked, errors = validate_repository(self.root)
        self.assertEqual(checked, 3)
        self.assertEqual(len(errors), 3)
        for expected in ("bad-utf8.md: cannot read strict UTF-8", "bad-newline.md: use LF",
                         "bad-text.md: contains replacement character U+FFFD"):
            self.assertTrue(any(expected in error for error in errors), errors)

    def test_invalid_json_and_nonstandard_numeric_constant_fail(self):
        self.write("broken.json", '{"missing": }')
        self.write("nonstandard.json", '{"value": NaN}')
        checked, errors = validate_repository(self.root)
        self.assertEqual(checked, 2)
        self.assertEqual(len(errors), 2)
        self.assertTrue(all("invalid JSON" in error for error in errors), errors)

    def test_untracked_files_are_not_checked(self):
        self.write("README.md", "# Valid\n")
        self.write("untracked.md", b"\xff", tracked=False)
        self.write("untracked.json", "invalid", tracked=False)
        self.assertEqual(validate_repository(self.root), (1, []))

    def test_cli_returns_failure_and_names_the_affected_file(self):
        self.write("README.md", "[missing](does-not-exist.md)\n")
        script = Path(__file__).resolve().parents[1] / "scripts" / "validate_repository.py"
        result = subprocess.run(
            [sys.executable, str(script), "--root", str(self.root)],
            capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("README.md:1: missing local link", result.stdout)


if __name__ == "__main__":
    unittest.main()
