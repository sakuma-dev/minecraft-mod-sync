"""Windows PowerShell 5.1向け入口の文字コードと構文を画面起動なしで確認する。"""

import base64
import codecs
from pathlib import Path
import shutil
import subprocess
import unittest


SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"


class PowerShellScriptTests(unittest.TestCase):
    def test_non_ascii_scripts_have_utf8_bom(self):
        for path in SCRIPTS.glob("*.ps1"):
            with self.subTest(script=path.name):
                raw = path.read_bytes()
                raw.decode("utf-8-sig", errors="strict")
                if not raw.isascii():
                    self.assertTrue(raw.startswith(codecs.BOM_UTF8), path.name)

    @unittest.skipUnless(shutil.which("powershell.exe"), "Windows PowerShell 5.1が必要")
    def test_windows_powershell_51_parses_scripts_without_running_them(self):
        # -EncodedCommandで外側のシェルの引用符処理と文字コードに依存させない。
        command = """$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { exit 2 }
$failed = $false
Get-ChildItem -LiteralPath . -Filter '*.ps1' | ForEach-Object {
    $tokens = $null; $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    Write-Output ($_.Name + ': ParseErrors=' + $errors.Count)
    if ($errors.Count) { $failed = $true; $errors | Format-List }
}
if ($failed) { exit 1 }
"""
        encoded = base64.b64encode(command.encode("utf-16le")).decode("ascii")
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-EncodedCommand", encoded],
            cwd=SCRIPTS, capture_output=True, timeout=30,
        )
        self.assertEqual(result.returncode, 0, (result.stdout + result.stderr).decode(errors="replace"))


if __name__ == "__main__":
    unittest.main()
