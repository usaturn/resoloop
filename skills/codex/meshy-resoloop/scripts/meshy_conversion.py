"""Locked, offline GLB -> editable Blender source -> fresh ResoLoop bundle."""

import hashlib
import json
import math
import os
import shutil
import signal
import subprocess
import tempfile
from pathlib import Path

from meshy_workflow import (
    WorkflowError,
    load_operation,
    operation_dir,
    operation_lock,
    safe_path,
    save_operation,
    select_glb_entry,
    validate_project,
)

RESOLOOP_PROCESS_TIMEOUT_SECONDS = 310


def source_glb(project, directory, operation):
    downloads = operation["downloads"]
    if downloads.get("state") != "completed":
        raise WorkflowError("completed_downloads_required")
    entry = select_glb_entry(downloads.get("files"))
    if entry is None:
        raise WorkflowError("unique_glb_required")
    path = safe_path(project, Path(entry["path"]))
    if not path.is_relative_to(directory / "source"):
        raise WorkflowError("source_outside_operation")
    if not path.is_file():
        raise WorkflowError("source_glb_missing")
    if (
        type(entry.get("bytes")) is not int
        or path.stat().st_size != entry["bytes"]
        or hashlib.sha256(path.read_bytes()).hexdigest() != entry.get("sha256")
    ):
        raise WorkflowError("source_glb_changed")
    return path


def run_resoloop(project, args, phase):
    # No credentials are required or passed into Blender or the offline exporter.
    env = {k: v for k, v in os.environ.items() if not k.startswith("MESHY_")}
    child = None
    try:
        child = subprocess.Popen(
            ["resoloop", "blender", *args, "--command-timeout", "300", "--json"],
            cwd=project,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            start_new_session=True,
        )
        stdout, stderr = child.communicate(timeout=RESOLOOP_PROCESS_TIMEOUT_SECONDS)
        # Keep cleanup byte-only so invalid output retains the phase error.
        stderr.decode("utf-8")
        envelope = json.loads(stdout.decode("utf-8"))
        if (
            child.returncode != 0
            or not isinstance(envelope, dict)
            or envelope.get("ok") is not True
        ):
            raise WorkflowError("resoloop_" + phase + "_failed")
    except (OSError, ValueError, subprocess.TimeoutExpired):
        raise WorkflowError("resoloop_" + phase + "_failed") from None
    finally:
        if child is not None:
            # Kill descendants too, even if the parent exited first. Otherwise a
            # timed-out Blender could recreate files after staging is removed.
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            child.communicate()


def check_tree(project, directory):
    for path in directory.rglob("*"):
        safe_path(project, path)


def finalize_reports(project, temporary, target):
    blend = temporary / "model.blend"
    report_path = temporary / "conversion-report.json"
    bundle = temporary / "bundle"
    if not all(
        path.is_file()
        for path in (
            blend,
            report_path,
            bundle / "model.apply.json",
            bundle / "report.json",
        )
    ):
        raise WorkflowError("conversion_outputs_missing")
    check_tree(project, temporary)
    report = json.loads(report_path.read_text())
    if report.get("state") != "completed":
        raise WorkflowError("conversion_outputs_missing")
    document = json.loads((bundle / "model.apply.json").read_text())
    assets = document.get("assets")
    if not isinstance(assets, dict) or not assets:
        raise WorkflowError("conversion_outputs_missing")
    for asset in assets.values():
        path = safe_path(project, bundle / asset["source"])
        if not path.is_relative_to(bundle) or not path.is_file():
            raise WorkflowError("bundle_asset_missing")
    # Export uses relative asset sources. Only provenance paths need rebasing.
    report["blend"] = str(target / "model.blend")
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, allow_nan=False, indent=2) + "\n"
    )
    export_path = bundle / "report.json"
    exported = json.loads(export_path.read_text())
    exported.update(
        source=str(target / "model.blend"),
        outputDirectory=str(target / "bundle"),
        applyFile=str(target / "bundle/model.apply.json"),
    )
    export_path.write_text(
        json.dumps(exported, ensure_ascii=False, allow_nan=False, indent=2) + "\n"
    )


def convert_operation(
    project: Path,
    name: str,
    *,
    height: float,
    yaw: float,
    origin: str,
    parent: str,
    model_name: str,
    allow_culling_change: bool = False,
) -> dict:
    if (
        not math.isfinite(height)
        or height <= 0
        or not math.isfinite(yaw)
        or origin not in {"ground", "center"}
        or not isinstance(parent, str)
        or not parent.strip()
        or not isinstance(model_name, str)
        or not model_name.strip()
        or any(c in model_name for c in "/\\\r\n")
    ):
        raise WorkflowError("invalid_conversion_parameters")
    project = validate_project(project)
    with operation_lock(project, name):
        directory = operation_dir(project, name)
        operation = load_operation(project, name)
        target = safe_path(project, directory / "converted")
        if target.exists() or "conversion" in operation:
            raise WorkflowError("conversion_already_exists")
        source = source_glb(project, directory, operation)
        temporary = Path(tempfile.mkdtemp(prefix=".conversion-", dir=directory))
        published = False
        try:
            blend = temporary / "model.blend"
            report = temporary / "conversion-report.json"
            script = Path(__file__).with_name("meshy_blender.py")
            argv = ["run", str(script)]
            for flag, value in (
                ("input", source),
                ("output", blend),
                ("report", report),
                ("height", height),
                ("yaw", yaw),
                ("origin", origin),
                ("name", model_name),
            ):
                argv += ["--arg=--" + flag, "--arg=" + str(value)]
            if allow_culling_change:
                argv.append("--arg=--allow-culling-change")
            try:
                run_resoloop(project, argv, "conversion")
            except WorkflowError:
                if report.is_file():
                    rejected = json.loads(report.read_text())
                    if rejected.get("state") == "rejected":
                        raise WorkflowError(
                            f"blender_rejected:{rejected['name']}:{rejected['reason']}"
                        ) from None
                raise
            run_resoloop(
                project,
                [
                    "export",
                    str(blend),
                    "--output",
                    str(temporary / "bundle"),
                    "--preserve-hierarchy",
                    "--pack-pbr",
                    "--parent",
                    parent,
                    "--name",
                    model_name,
                ],
                "export",
            )
            source_glb(
                project, directory, operation
            )  # Source must still match after processing.
            finalize_reports(project, temporary, target)
            conversion = {
                "state": "completed",
                "blend": str(target / "model.blend"),
                "report": str(target / "conversion-report.json"),
                "bundle": str(target / "bundle"),
                "height": height,
                "yaw": yaw,
                "origin": origin,
                "parent": parent,
                "name": model_name,
                "allow_culling_change": allow_culling_change,
                "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            }
            safe_path(project, target)
            if target.exists():
                raise WorkflowError("conversion_already_exists")
            os.rename(temporary, target)
            published = True
            operation["conversion"] = conversion
            save_operation(project, name, operation)
            return conversion
        except BaseException:
            if published:
                # save_operation may fail on directory fsync after its replace.
                # Restore the prior manifest as well as removing staged output.
                operation.pop("conversion", None)
                try:
                    save_operation(project, name, operation)
                finally:
                    shutil.rmtree(target)
            raise
        finally:
            if temporary.exists():
                shutil.rmtree(temporary)
