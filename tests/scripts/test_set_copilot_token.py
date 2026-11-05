"""Offline tests for scripts/set-copilot-token.sh.

Fake az, curl, and sleep commands replace the real ones. No test calls Azure or GitHub.
Run: python3 -m unittest discover -s tests/scripts
"""

import os
import pathlib
import stat
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "set-copilot-token.sh"
FAKE_TOKEN = "github_pat_FAKE_TEST_ONLY_0000"

FAKE_AZ = """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$FAKE_LOG/az-args.log"
case "$*" in
  *"secret set"*) cat > "$FAKE_LOG/az-stdin.txt" ;;
  *"revision list"*) echo "api--0000001" ;;
  *"containerapp show"*) echo "api.example.invalid" ;;
esac
"""

FAKE_CURL = """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$FAKE_LOG/curl-args.log"
echo "{\\"aiAvailable\\":$FAKE_AI}"
"""

FAKE_SLEEP = "#!/usr/bin/env bash\nexit 0\n"


class SetCopilotTokenTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.tmp.name)
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        for name, body in (("az", FAKE_AZ), ("curl", FAKE_CURL), ("sleep", FAKE_SLEEP)):
            path = self.bin / name
            path.write_text(body)
            path.chmod(path.stat().st_mode | stat.S_IXUSR)

    def tearDown(self):
        self.tmp.cleanup()

    def run_script(self, stdin_text, *args, ai="true"):
        env = dict(os.environ)
        env["PATH"] = f"{self.bin}{os.pathsep}{env['PATH']}"
        env["FAKE_LOG"] = str(self.dir)
        env["FAKE_AI"] = ai
        return subprocess.run(
            ["bash", str(SCRIPT), *args],
            input=stdin_text,
            capture_output=True,
            text=True,
            env=env,
            timeout=30,
        )

    def read(self, name):
        path = self.dir / name
        return path.read_text() if path.exists() else ""

    def test_token_goes_through_stdin_and_never_in_arguments(self):
        result = self.run_script(FAKE_TOKEN + "\n")

        self.assertEqual(result.returncode, 0, result.stderr)
        az_args = self.read("az-args.log")
        self.assertNotIn(FAKE_TOKEN, az_args)
        self.assertNotIn(FAKE_TOKEN, self.read("curl-args.log"))
        self.assertNotIn(FAKE_TOKEN, result.stdout + result.stderr)
        self.assertIn("--secrets @/dev/stdin", az_args)
        self.assertEqual(self.read("az-stdin.txt"), f"copilot-github-token={FAKE_TOKEN}")
        self.assertIn("revision restart -g rg-haru -n api --revision api--0000001", az_args)

    def test_clear_sets_unset_and_expects_ai_off(self):
        result = self.run_script("", "--clear", ai="false")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.read("az-stdin.txt"), "copilot-github-token=unset")
        self.assertIn("aiAvailable=false", result.stdout)

    def test_rejected_tokens_never_reach_az(self):
        for token in (
            "ghp_FAKE_CLASSIC_TOKEN",
            "gho_FAKE_LOGIN_TOKEN",
            "",
            FAKE_TOKEN + FAKE_TOKEN,
            FAKE_TOKEN + " x",
        ):
            with self.subTest(prefix=token[:4] or "empty"):
                (self.dir / "az-args.log").unlink(missing_ok=True)
                result = self.run_script(token + "\n")

                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.read("az-args.log"), "")
                if token:
                    self.assertNotIn(token, result.stdout + result.stderr)

    def test_fails_when_ai_does_not_turn_on(self):
        result = self.run_script(FAKE_TOKEN + "\n", ai="false")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("did not become true", result.stderr)


if __name__ == "__main__":
    unittest.main()
