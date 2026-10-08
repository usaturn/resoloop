"""Read-only optional-dependency probes. Never import the POSIX workflow here."""

import json
import os
import re
import sys
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from pathlib import Path

PROBE_TIMEOUT_SECONDS = 10


class WorkflowError(Exception):
    """Only a wrapper-controlled error code may cross the CLI boundary."""


def require_supported_platform():
    if sys.platform != "linux":
        raise WorkflowError("unsupported_platform_use_linux_or_wsl2")


def _probe_env():
    blocked = {
        "NODE_OPTIONS", "NODE_DEBUG", "NODE_DEBUG_NATIVE", "DEBUG",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
    }
    return {k: v for k, v in os.environ.items()
            if not k.upper().startswith("MESHY_") and k.upper() not in blocked}


def _probe(argv, *, project=None):
    """Return captured text internally only; no child output crosses our boundary."""
    import subprocess

    child = None
    completed = False
    try:
        child = subprocess.Popen(
            argv, env=_probe_env(), cwd=project, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            start_new_session=True,
        )
        stdout, stderr = child.communicate(timeout=PROBE_TIMEOUT_SECONDS)
        # Decode once here, never during stop/reap: malformed output must not
        # replace a failed check with a second cleanup-time UnicodeError.
        stdout = stdout.decode("utf-8")
        stderr.decode("utf-8")
        completed = True
        return stdout if child.returncode == 0 else None
    except (OSError, UnicodeError, subprocess.TimeoutExpired):
        return None
    finally:
        if child is not None and not completed:
            # Import POSIX-specific process handling only after the platform gate.
            import signal
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            child.communicate()


def _cli_checks():
    node = _probe(["node", "--version"])
    version = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", (node or "").strip())
    node_ok = bool(version and tuple(map(int, version.groups())) >= (22, 12, 0))
    meshy = _probe(["meshy", "--no-update-check", "--version"])
    return node_ok, (meshy or "").strip() == "0.4.0"


def require_cli_compatibility() -> None:
    require_supported_platform()
    node_ok, meshy_ok = _cli_checks()
    if not node_ok:
        raise WorkflowError("cli_compatibility_node_requires_22_12_0")
    if not meshy_ok:
        raise WorkflowError("cli_compatibility_meshy_requires_0_4_0")


def diagnose(project: "Path") -> dict:
    checks = []

    def check(name, ok, repair):
        checks.append({"name": name, "ok": bool(ok), "repair": repair})

    check("platform", sys.platform == "linux", "Use Linux or the Linux side of WSL2.")
    key_present = bool(os.environ.get("MESHY_API_KEY", "").strip())
    if sys.platform != "linux":
        return {"ok": False, "checks": checks, "key_present": key_present}
    from pathlib import Path

    check("python", sys.version_info >= (3, 12), "Use uv with Python >=3.12.")
    # Read only: do not create a project, config, operation or temporary script.
    project = Path(project).absolute()
    check("project", project.is_dir() and (project / ".resoloop.json").is_file(),
          "Select an initialized ResoLoop project with --project.")
    uv = _probe(["uv", "--version"])
    check("uv", bool(re.fullmatch(r"uv \d+\.\d+\.\d+(?:[^\r\n]*)", (uv or "").strip())),
          "Install uv manually, then run uv --version.")
    node_ok, meshy_ok = _cli_checks()
    check("node", node_ok, "Install Node.js >=22.12.0; Node 24 is recommended.")
    check("meshy", meshy_ok, "Manually install meshy-cli@0.4.0; do not auto-update.")
    version = _probe(["resoloop", "--version"])
    check("resoloop", bool(re.fullmatch(r"(?:resoloop )?\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?",
                                      (version or "").strip())),
          "Install/update ResoLoop to the latest public NuGet version, including prereleases.")
    help_text = _probe(["resoloop", "blender", "export", "--help"])
    check("exporter", bool(help_text and "--preserve-hierarchy" in help_text and "--pack-pbr" in help_text),
          "Update ResoLoop and check blender export --help for hierarchy/PBR support.")
    found = _probe(["resoloop", "blender", "find", "--json"],
                   project=project if project.is_dir() else None)
    executable = None
    try:
        envelope = json.loads(found or "")
        candidate = envelope["data"]["executable"]
        if envelope["ok"] is True and isinstance(candidate, str) and Path(candidate).is_file():
            executable = candidate
    except (ValueError, KeyError, TypeError):
        pass
    check("blender", executable is not None,
          "Install Blender manually or set RESOLOOP_BLENDER_EXECUTABLE; run resoloop blender find --json.")
    # No save, exporter execution or script file: import in the selected Blender Python.
    expression = "import bpy, numpy; print('RESOLOOP_MESHY_BPY_NUMPY_OK')"
    imported = _probe([executable, "--background", "--factory-startup", "--disable-autoexec",
                       "--python-exit-code", "1", "--python-expr", expression]) if executable else None
    check("bpy_numpy", bool(imported and "RESOLOOP_MESHY_BPY_NUMPY_OK" in imported.splitlines()),
          "Install NumPy for Blender's own Python, not only for the wrapper's Python; re-run doctor.")
    directory = Path(__file__).parent
    resources = ("meshy.py", "meshy_workflow.py", "meshy_conversion.py", "meshy_blender.py",
                 "meshy_cli_guard.mjs", "meshy_doctor.py")
    check("resources", all((directory / name).is_file() for name in resources),
          "Review resoloop skills sync --check, reconcile conflicts, then sync --update.")
    return {"ok": all(c["ok"] for c in checks), "checks": checks, "key_present": key_present}
