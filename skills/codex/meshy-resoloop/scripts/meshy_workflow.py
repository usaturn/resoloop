"""Env-only, single-submission Meshy workflow (standard library only).

The official CLI owns HTTP and its journal. This module owns the project write
boundary and refuses to infer that an uncertain POST is safe to repeat.
"""

import fcntl
import hashlib
import json
import math
import os
import re
import subprocess
import tempfile
import uuid
from contextlib import contextmanager
from pathlib import Path

from meshy_doctor import WorkflowError, require_cli_compatibility

CLI_TIMEOUT_SECONDS = 120
RESOURCES = ("text-to-3d", "image-to-3d")
STATUSES = {"PENDING", "IN_PROGRESS", "SUCCEEDED", "FAILED", "CANCELED"}
SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,127}\Z")


def _secrets():
    key = os.environ.get("MESHY_API_KEY", "")
    return {s for s in (key, key.strip()) if s}


def _contains_secret(value):
    return isinstance(value, str) and any(key in value for key in _secrets())


def redact(value):
    if isinstance(value, str):
        for key in sorted(_secrets(), key=len, reverse=True):
            value = value.replace(key, "<redacted>")
        return value
    if isinstance(value, dict):
        return {redact(k): redact(v) for k, v in value.items()}
    if isinstance(value, list):
        return [redact(v) for v in value]
    return value


def require_key():
    key = os.environ.get("MESHY_API_KEY", "").strip()
    normalized = re.sub(r"[^a-z0-9]", "", key.lower())
    if (
        not key
        or normalized in {"placeholder", "changeme", "meshyapikey"}
        or normalized.startswith("your")
        or key.startswith("<")
    ):
        raise WorkflowError("env_key_required")
    return key


def safe_path(project: Path, path: Path) -> Path:
    """Return a lexical absolute path inside project, rejecting every symlink.

    Check ancestors before resolving: resolve() alone would hide directory links.
    This protects static path attacks, not hostile concurrent directory renames.
    """
    project = Path(os.path.abspath(project))
    path = Path(path)
    path = Path(os.path.abspath(path if path.is_absolute() else project / path))
    if not path.is_relative_to(project):
        raise WorkflowError("path_outside_project")
    for component in reversed((path, *path.parents)):
        if component.is_symlink():
            raise WorkflowError("symlink_forbidden")
    return path


def validate_project(project):
    project = Path(os.path.abspath(project))
    project = safe_path(project, project)
    marker = safe_path(project, project / ".resoloop.json")
    if not project.is_dir() or not marker.is_file():
        raise WorkflowError("resoloop_project_required")
    return project


def _mkdir(project, path):
    path = safe_path(project, path)
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    return safe_path(project, path)


def _safe_tree(project, path):
    path = safe_path(project, path)
    if path.exists():
        if not path.is_dir():
            raise WorkflowError("directory_required")
        for root, dirs, files in os.walk(path, followlinks=False):
            for name in dirs + files:
                safe_path(project, Path(root) / name)
    return path


def operation_dir(project: Path, name: str) -> Path:
    project = validate_project(project)
    if not SEGMENT.fullmatch(name) or _contains_secret(name):
        raise WorkflowError("invalid_operation_name")
    return safe_path(project, project / "content/generated/meshy" / name)


@contextmanager
def operation_lock(project: Path, name: str):
    directory = _mkdir(project, operation_dir(project, name))
    path = safe_path(project, directory / ".lock")
    fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise WorkflowError("operation_locked") from None
        yield
    finally:
        os.close(fd)


def load_operation(project: Path, name: str) -> dict:
    path = safe_path(project, operation_dir(project, name) / "operation.json")
    try:
        operation = json.loads(path.read_text())
        if (
            not isinstance(operation, dict)
            or operation.get("schema_version") != 1
            or operation.get("resource") not in RESOURCES
            or operation.get("kind") not in {"text", "image", "refine"}
            or not isinstance(operation.get("request"), dict)
            or not isinstance(operation.get("task"), dict)
            or not isinstance(operation.get("downloads"), dict)
        ):
            raise ValueError
        if not isinstance(operation.get("operation_id"), str):
            raise TypeError
        uuid.UUID(operation["operation_id"])
    except (OSError, ValueError, KeyError, TypeError):
        raise WorkflowError("invalid_or_missing_operation") from None
    return operation


def save_operation(project: Path, name: str, operation: dict) -> None:
    directory = operation_dir(project, name)
    target = safe_path(project, directory / "operation.json")
    fd, temporary = tempfile.mkstemp(
        prefix=".operation-", suffix=".json", dir=directory
    )
    try:
        with os.fdopen(fd, "w") as handle:
            json.dump(redact(operation), handle, indent=2, allow_nan=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        safe_path(project, target)
        os.replace(temporary, target)
        directory_fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def _task_id(value):
    return (
        value
        if isinstance(value, str)
        and SEGMENT.fullmatch(value)
        and not _contains_secret(value)
        else None
    )


def _number(value):
    try:
        return value if type(value) in (int, float) and math.isfinite(value) else None
    except OverflowError:
        return None


def _task(result):
    view = result.get("task") if isinstance(result.get("task"), dict) else {}
    submission = (
        result.get("submission") if isinstance(result.get("submission"), dict) else {}
    )
    task_id = (
        _task_id(view.get("task_id"))
        or _task_id(result.get("task_id"))
        or _task_id(submission.get("task_id"))
    )
    return {
        "task_id": task_id,
        "status": view.get("status")
        if isinstance(view.get("status"), str) and view["status"] in STATUSES
        else None,
        "progress": _number(view.get("progress")),
    }


def _error(envelope):
    error = envelope.get("error")
    code = error.get("code") if isinstance(error, dict) else None
    return {
        "code": code
        if isinstance(code, str)
        and re.fullmatch(r"[a-z_]{1,64}", code)
        and not _contains_secret(code)
        else "cli_failed"
    }


def child_env(project):
    key = require_key()
    require_cli_compatibility()  # Before config/journal creation or submitting stage.
    config = _safe_tree(project, project / ".resoloop/meshy-cli")
    _mkdir(project, config / "operations/locks")
    credentials = safe_path(project, config / "env-only-no-credentials.json")
    if credentials.exists():
        raise WorkflowError("isolated_credentials_must_not_exist")
    blocked = {
        "NODE_OPTIONS",
        "NODE_DEBUG",
        "NODE_DEBUG_NATIVE",
        "DEBUG",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
    }
    env = {
        k: v
        for k, v in os.environ.items()
        if not k.startswith("MESHY_") and k.upper() not in blocked
    }
    env.update(
        NODE_OPTIONS=f"--import {json.dumps(str(Path(__file__).with_name('meshy_cli_guard.mjs')))}",
        MESHY_API_KEY=key,
        MESHY_CONFIG_DIR=str(config),
        MESHY_CREDENTIALS_PATH=str(credentials),
        MESHY_BASE_URL_V1="https://api.meshy.ai/openapi/v1",
        MESHY_BASE_URL_V2="https://api.meshy.ai/openapi/v2",
        MESHY_LOG_LEVEL="silent",
        MESHY_CLI_NO_UPDATE_NOTIFIER="1",
    )
    return env


def _cli(project, args, env, timeout=CLI_TIMEOUT_SECONDS):
    argv = [
        "meshy",
        "--output-schema",
        "v1",
        "--format",
        "json",
        "--no-update-check",
        "--workspace",
        str(project),
        *args,
    ]
    if any(_contains_secret(arg) for arg in argv):
        raise WorkflowError("secret_in_cli_arguments")
    child = None
    try:
        child = subprocess.Popen(
            argv,
            env=env,
            cwd=project,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        stdout, _ = child.communicate(timeout=timeout)
        envelope = json.loads(stdout)
        if (
            not isinstance(envelope, dict)
            or envelope.get("schema_version") != "meshy.cli/v1"
            or type(envelope.get("ok")) is not bool
            or not isinstance(envelope.get("result"), dict)
        ):
            raise ValueError
        return envelope, child.returncode == 0 and envelope["ok"]
    except subprocess.TimeoutExpired:
        raise WorkflowError("cli_timeout") from None
    except (OSError, ValueError, UnicodeError):
        raise WorkflowError("cli_output_or_spawn_failed") from None
    finally:
        if child is not None and child.poll() is None:
            child.kill()
            child.communicate()


def _call(project, args, env, timeout=None):
    return _cli(project, args, env, CLI_TIMEOUT_SECONDS if timeout is None else timeout)


def plan(project, args):
    kind = args.kind
    model = args.model
    if not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", model):
        raise WorkflowError("invalid_model")
    payload = {"ai_model": model, "target_formats": ["glb"]}
    request = {"payload": payload}
    texturing = args.texture if args.texture is not None else kind == "refine"
    if kind == "text":
        if (
            not args.prompt
            or not args.prompt.strip()
            or len(args.prompt) > 600
            or args.image
            or args.preview_task_id
            or texturing
        ):
            raise WorkflowError("preview_requires_prompt_without_texture")
        payload.update(mode="preview", prompt=args.prompt)
    elif kind == "refine":
        if not _task_id(args.preview_task_id) or args.image or not texturing:
            raise WorkflowError("refine_requires_preview_task")
        payload.update(
            mode="refine",
            preview_task_id=args.preview_task_id,
            enable_pbr=args.pbr,
            texture_resolution=args.texture_resolution,
            remove_lighting=True,
        )
        if args.prompt:
            if len(args.prompt) > 600:
                raise WorkflowError("prompt_too_long")
            payload["texture_prompt"] = args.prompt
    else:
        if not args.image or args.prompt or args.preview_task_id:
            raise WorkflowError("image_requires_local_file")
        image = safe_path(project, Path(args.image))
        if not image.is_file():
            raise WorkflowError("image_file_required")
        request["image"] = {
            "path": str(image),
            "sha256": hashlib.sha256(image.read_bytes()).hexdigest(),
        }
        payload.update(
            should_texture=texturing, ultra_mode=False, image_enhancement=True
        )
        if texturing:
            payload.update(
                enable_pbr=args.pbr,
                texture_resolution=args.texture_resolution,
                remove_lighting=True,
            )
    if _contains_secret(json.dumps(request)):
        raise WorkflowError("secret_in_plan")
    operation = {
        "schema_version": 1,
        "operation_id": str(uuid.uuid4()),
        "kind": kind,
        "resource": "image-to-3d" if kind == "image" else "text-to-3d",
        "request": request,
        "stage": "planned",
        "task": {"task_id": None, "status": None, "progress": None},
        "downloads": {"state": "not_requested", "files": []},
    }
    target = safe_path(
        project, operation_dir(project, args.operation) / "operation.json"
    )
    try:
        fd = os.open(
            target, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600
        )
    except FileExistsError:
        raise WorkflowError("operation_already_exists") from None
    os.close(fd)
    save_operation(project, args.operation, operation)
    return operation


def _record_task(operation, result, expected=None):
    task = _task(result)
    if not task["task_id"] or (expected and task["task_id"] != expected):
        raise WorkflowError("task_id_missing_or_mismatch")
    operation["task"] = task
    status = task["status"]
    if status == "SUCCEEDED":
        operation["stage"] = (
            "downloaded"
            if operation["downloads"]["state"] == "completed"
            else "succeeded"
        )
    elif status in {"FAILED", "CANCELED"}:
        operation["stage"] = status.lower()
    else:
        operation["stage"] = "submitted"


def _validate_request(operation):
    """A saved plan is not an arbitrary JSON passthrough surface."""
    kind = operation["kind"]
    request = operation["request"]
    payload = request.get("payload")
    if not isinstance(payload, dict) or _contains_secret(json.dumps(request)):
        raise WorkflowError("invalid_request")
    allowed = {"ai_model", "target_formats"}
    if kind == "text":
        allowed |= {"mode", "prompt"}
        valid = payload.get("mode") == "preview"
        prompt = payload.get("prompt")
        valid = (
            valid
            and isinstance(prompt, str)
            and bool(prompt.strip())
            and len(prompt) <= 600
        )
    elif kind == "refine":
        allowed |= {
            "mode",
            "preview_task_id",
            "enable_pbr",
            "texture_resolution",
            "remove_lighting",
        }
        valid = payload.get("mode") == "refine" and bool(
            _task_id(payload.get("preview_task_id"))
        )
        if "texture_prompt" in payload:
            allowed.add("texture_prompt")
            valid = (
                valid
                and isinstance(payload["texture_prompt"], str)
                and len(payload["texture_prompt"]) <= 600
            )
    else:
        allowed |= {"should_texture", "ultra_mode", "image_enhancement"}
        valid = (
            type(payload.get("should_texture")) is bool
            and payload.get("ultra_mode") is False
            and payload.get("image_enhancement") is True
        )
        if payload.get("should_texture") is True:
            allowed |= {"enable_pbr", "texture_resolution", "remove_lighting"}
        image = request.get("image")
        valid = (
            valid
            and isinstance(image, dict)
            and set(image) == {"path", "sha256"}
            and isinstance(image["path"], str)
            and isinstance(image["sha256"], str)
            and bool(re.fullmatch(r"[0-9a-f]{64}", image["sha256"]))
        )
    if "enable_pbr" in allowed:
        valid = (
            valid
            and type(payload.get("enable_pbr")) is bool
            and payload.get("texture_resolution") in ("2k", "4k", "8k")
            and payload.get("remove_lighting") is True
        )
    resource = "image-to-3d" if kind == "image" else "text-to-3d"
    model = payload.get("ai_model")
    if (
        not valid
        or set(payload) != allowed
        or operation["resource"] != resource
        or set(request) != ({"payload", "image"} if kind == "image" else {"payload"})
        or payload.get("target_formats") != ["glb"]
        or not isinstance(model, str)
        or not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", model)
    ):
        raise WorkflowError("invalid_request")


def submit(project, name, operation, env, confirmed):
    if not confirmed:
        raise WorkflowError("confirm_paid_required")
    if operation["task"].get("task_id"):
        return operation  # Not even a GET is necessary; never create again.
    if operation["stage"] != "planned":
        if operation["stage"] == "submitting":
            operation["stage"] = "unknown"
            save_operation(project, name, operation)
        raise WorkflowError("unknown_use_list_and_attach")
    _validate_request(operation)
    request = operation["request"]
    args = [
        operation["resource"],
        "create",
        "--async",
        "--operation-id",
        operation["operation_id"],
        "--data",
        json.dumps(request["payload"]),
    ]
    if operation["kind"] == "image":
        image = safe_path(project, Path(request["image"]["path"]))
        if (
            not image.is_file()
            or hashlib.sha256(image.read_bytes()).hexdigest()
            != request["image"]["sha256"]
        ):
            raise WorkflowError("input_image_changed")
        args += ["--image-url", str(image)]
    if operation["kind"] == "refine":
        preview_id = request["payload"]["preview_task_id"]
        envelope, ok = _call(project, ["text-to-3d", "get", preview_id], env)
        view = envelope["result"].get("task", {})
        if (
            not ok
            or not isinstance(view, dict)
            or _task(envelope["result"])["task_id"] != preview_id
            or view.get("status") != "SUCCEEDED"
            or view.get("type") != "text-to-3d-preview"
            or view.get("resource") != "text-to-3d"
        ):
            raise WorkflowError("successful_text_preview_required")
    operation["stage"] = "submitting"
    save_operation(project, name, operation)
    try:
        envelope, ok = _call(project, args, env)
        task = _task(envelope["result"])
        if task["task_id"]:
            operation["task"] = task
            operation["stage"] = "submitted"
        else:
            operation["stage"] = "unknown"
            ok = False
        if not ok:
            operation["error"] = _error(envelope)
        else:
            operation.pop("error", None)
        save_operation(project, name, operation)
    except (WorkflowError, KeyboardInterrupt):
        operation["stage"] = "unknown"
        save_operation(project, name, operation)
        raise
    if not ok:
        raise WorkflowError(
            operation.get("error", {}).get("code", "submission_unknown")
        )
    return operation


def query(project, name, operation, env, verb, task_id=None, timeout=600):
    task_id = _task_id(task_id or operation["task"].get("task_id"))
    if not task_id:
        raise WorkflowError("existing_task_required")
    if verb == "attach" and operation["task"].get("task_id") not in (None, task_id):
        raise WorkflowError("task_already_attached")
    args = [operation["resource"], "wait" if verb == "wait" else "get", task_id]
    if verb == "wait":
        args += ["--timeout", str(timeout)]
    envelope, ok = _call(
        project,
        args,
        env,
        max(CLI_TIMEOUT_SECONDS, timeout + 5) if verb == "wait" else None,
    )
    if verb == "attach":
        result = envelope["result"]
        # Attach identity guard: reject before recording or saving any state.
        if _task(result)["task_id"] != task_id:
            raise WorkflowError("task_id_missing_or_mismatch")
        view = result.get("task")
        expected_type = {
            "text": "text-to-3d-preview",
            "refine": "text-to-3d-refine",
            "image": "image-to-3d",
        }[operation["kind"]]
        # Attach kind/resource guard: known status/wait may still be partial.
        if (
            not isinstance(view, dict)
            or view.get("type") != expected_type
            or view.get("resource") != operation["resource"]
        ):
            raise WorkflowError("attached_task_kind_or_resource_mismatch")
        # Attach preview identity guard: independent of the returned task type.
        if operation["kind"] == "refine" and task_id == operation["request"][
            "payload"
        ].get("preview_task_id"):
            raise WorkflowError("refine_task_must_differ_from_preview")
    _record_task(operation, envelope["result"], task_id)
    if not ok:
        operation["error"] = _error(envelope)
    else:
        operation.pop("error", None)
    save_operation(project, name, operation)
    if (
        not ok
        or operation["task"]["status"] in {"FAILED", "CANCELED"}
        or (verb == "wait" and operation["task"]["status"] != "SUCCEEDED")
    ):
        raise WorkflowError(
            operation.get("error", {}).get("code", "task_not_successful")
        )
    return operation


def select_glb_entry(files):
    """Select the official primary, or a single legacy unkeyed GLB.

    A keyed backup must never replace a missing/failed primary. Include failed
    entries when checking keys and primary uniqueness, then require usable status.
    """
    if not isinstance(files, list):
        return None
    glbs = [
        entry
        for entry in files
        if isinstance(entry, dict)
        and isinstance(entry.get("path"), str)
        and Path(entry["path"]).suffix.lower() == ".glb"
    ]
    if any("key" in entry for entry in glbs):
        candidates = [entry for entry in glbs if entry.get("key") == "model.glb"]
    else:
        candidates = [
            entry for entry in glbs if entry.get("status") in {"written", "skipped"}
        ]
    if len(candidates) != 1 or candidates[0].get("status") not in {
        "written",
        "skipped",
    }:
        return None
    return candidates[0]


def _download_files(project, source, files):
    clean = []
    invalid = not isinstance(files, list)
    for entry in files if isinstance(files, list) else []:
        try:
            if not isinstance(entry, dict) or _contains_secret(json.dumps(entry)):
                raise WorkflowError("invalid_download_entry")
            path = safe_path(project, Path(entry["path"]))
            if not path.is_relative_to(source):
                raise WorkflowError("download_outside_source")
            status = entry.get("status")
            if status not in {"written", "skipped", "failed"}:
                raise WorkflowError("invalid_download_status")
            if status != "failed" and not path.is_file():
                raise WorkflowError("download_file_missing")
            item = {
                "path": str(path),
                "format": path.suffix.lstrip(".").lower(),
                "status": status,
            }
            if "key" in entry:
                if not isinstance(entry["key"], str):
                    raise WorkflowError("invalid_download_entry")
                item["key"] = entry["key"]
            if status != "failed":
                content = path.read_bytes()
                item.update(
                    bytes=len(content), sha256=hashlib.sha256(content).hexdigest()
                )
            clean.append(item)
        except (WorkflowError, KeyError, TypeError, OSError):
            invalid = True
    return clean, invalid


def download(project, name, operation, env, timeout=None):
    task_id = _task_id(operation["task"].get("task_id"))
    if not task_id or operation["task"].get("status") != "SUCCEEDED":
        raise WorkflowError("successful_existing_task_required")
    source = _safe_tree(project, operation_dir(project, name) / "source")
    _mkdir(project, source)
    operation["downloads"]["state"] = "downloading"
    operation["stage"] = "succeeded"
    save_operation(project, name, operation)
    try:
        envelope, ok = _call(
            project,
            [
                "download",
                "--resource",
                operation["resource"],
                "--task-id",
                task_id,
                "--all",
                "--overwrite",
                "--output-dir",
                str(source),
            ],
            env,
            timeout,
        )
        manifest = envelope["result"].get("downloads")
        if not isinstance(manifest, dict):
            raise WorkflowError("download_manifest_required")
        files, invalid = _download_files(project, source, manifest.get("files"))
        complete = (
            ok
            and not invalid
            and manifest.get("state") == "completed"
            and bool(files)
            and all(f["status"] in {"written", "skipped"} for f in files)
            and select_glb_entry(files) is not None
        )
        operation["downloads"] = {
            "state": "completed" if complete else ("partial" if files else "failed"),
            "files": files,
        }
        if complete:
            operation["stage"] = "downloaded"
            operation.pop("error", None)
        else:
            operation["stage"] = "succeeded"
            operation["error"] = _error(envelope)
        save_operation(project, name, operation)
    except (WorkflowError, KeyboardInterrupt):
        operation["downloads"]["state"] = (
            "partial" if operation["downloads"]["files"] else "failed"
        )
        operation["stage"] = "succeeded"
        save_operation(project, name, operation)
        raise
    if not complete:
        raise WorkflowError("download_incomplete")
    return operation


def execute(args):
    project = validate_project(Path(args.project))
    if args.command != "plan":
        require_key()  # Before any possible credential fallback or API execution.
    if args.command in {"list", "balance"}:
        env = child_env(project)
        envelope, ok = _call(
            project,
            [args.resource, "list"] if args.command == "list" else ["balance"],
            env,
        )
        if not ok:
            raise WorkflowError(_error(envelope)["code"])
        if args.command == "balance":
            balance = _number(envelope["result"].get("balance"))
            if balance is None:
                raise WorkflowError("numeric_balance_required")
            return {"balance": balance}
        items = envelope["result"].get("items")
        if not isinstance(items, list):
            raise WorkflowError("task_list_required")
        return {
            "items": [_task({"task": item}) for item in items if isinstance(item, dict)]
        }
    with operation_lock(project, args.operation):
        if args.command == "plan":
            return plan(project, args)
        operation = load_operation(project, args.operation)
        _safe_tree(project, operation_dir(project, args.operation))
        if args.command == "submit" and (
            not args.confirm_paid or operation["task"].get("task_id")
            or operation["stage"] != "planned"
        ):
            # These branches cannot send: retain single-submission recovery even
            # when the CLI is missing/incompatible, without preparing child_env.
            return submit(project, args.operation, operation, None, args.confirm_paid)
        env = child_env(project)
        if args.command == "submit":
            return submit(project, args.operation, operation, env, args.confirm_paid)
        if args.command == "download":
            return download(
                project, args.operation, operation, env, getattr(args, "timeout", None)
            )
        return query(
            project,
            args.operation,
            operation,
            env,
            args.command,
            getattr(args, "task_id", None),
            getattr(args, "timeout", 600),
        )
