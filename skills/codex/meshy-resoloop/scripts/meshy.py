"""Run with: uv run --no-project <skill>/scripts/meshy.py --project PROJECT COMMAND."""

import argparse
import json
import math
import signal
import sys

# Keep help and platform rejection usable without fcntl or other POSIX imports.
# Also avoid creating __pycache__ inside a deployed project during doctor/help.
sys.dont_write_bytecode = True
from meshy_doctor import WorkflowError, diagnose, require_supported_platform


class Parser(argparse.ArgumentParser):
    def error(self, message):
        # argparse otherwise echoes arbitrary user values (possibly a key).
        raise WorkflowError("invalid_arguments")


def boolean(value):
    if value not in ("true", "false"):
        raise argparse.ArgumentTypeError("expected true or false")
    return value == "true"


def seconds(value):
    try:
        value = float(value)
    except ValueError:
        raise argparse.ArgumentTypeError("expected seconds") from None
    if not math.isfinite(value) or value < 0:
        raise argparse.ArgumentTypeError("expected nonnegative finite seconds")
    return value


def parser():
    root = Parser(
        description="Plan offline; submit once with env-only authentication. Recover unknown submissions with list/attach."
    )
    root.add_argument("--project", required=True)
    commands = root.add_subparsers(dest="command", required=True, parser_class=Parser)
    plan = commands.add_parser(
        "plan", help="Create a new offline operation; never overwrite."
    )
    plan.add_argument("--operation", required=True)
    plan.add_argument("--kind", required=True, choices=("text", "image", "refine"))
    plan.add_argument("--prompt")
    plan.add_argument("--image")
    plan.add_argument("--preview-task-id")
    plan.add_argument("--model", default="latest")
    plan.add_argument("--texture", type=boolean)
    plan.add_argument("--pbr", type=boolean, default=True)
    plan.add_argument("--texture-resolution", choices=("2k", "4k", "8k"), default="2k")
    for verb in ("submit", "status", "wait", "download", "attach"):
        command = commands.add_parser(verb)
        command.add_argument("--operation", required=True)
        if verb == "submit":
            command.add_argument("--confirm-paid", action="store_true")
        if verb == "wait":
            command.add_argument("--timeout", type=seconds, default=600)
        if verb == "download":
            command.add_argument("--timeout", type=seconds, default=None)
        if verb == "attach":
            command.add_argument("--task-id", required=True)
    conversion = commands.add_parser(
        "convert", help="Convert a downloaded GLB offline; never apply or overwrite."
    )
    conversion.add_argument("--operation", required=True)
    conversion.add_argument("--height", type=float, required=True)
    conversion.add_argument("--yaw", type=float, default=0)
    conversion.add_argument("--origin", choices=("ground", "center"), default="ground")
    conversion.add_argument(
        "--parent",
        required=True,
        help="Previously verified selector; not checked online here.",
    )
    conversion.add_argument("--name")
    conversion.add_argument(
        "--allow-culling-change",
        action="store_true",
        help="Accept only unpreserved glTF doubleSided/backface culling; all other checks remain.",
    )
    listing = commands.add_parser(
        "list", help="Read recent tasks for manual reconciliation; never create."
    )
    listing.add_argument("--operation")
    listing.add_argument("--resource", required=True, choices=("text-to-3d", "image-to-3d"))
    balance = commands.add_parser(
        "balance", help="Explicit read-only API balance check."
    )
    balance.add_argument("--operation")
    commands.add_parser("doctor", help="Read-only local dependency checks; no API calls or key required.")
    return root


def _interrupt(signum, frame):
    raise KeyboardInterrupt


def main(argv=None):
    signal.signal(signal.SIGTERM, _interrupt)
    redact = lambda value: value  # Pre-import errors are fixed wrapper codes only.
    try:
        args = parser().parse_args(argv)
        require_supported_platform()
        from pathlib import Path
        if args.command == "doctor":
            result = diagnose(Path(args.project))
            print(json.dumps(result, allow_nan=False))
            return 0 if result["ok"] else 1
        from meshy_workflow import execute, redact
        if args.command == "convert":
            from meshy_conversion import convert_operation
            result = convert_operation(
                Path(args.project),
                args.operation,
                height=args.height,
                yaw=args.yaw,
                origin=args.origin,
                parent=args.parent,
                model_name=args.name if args.name is not None else args.operation,
                allow_culling_change=args.allow_culling_change,
            )
        else:
            result = execute(args)
        print(json.dumps(redact(result), allow_nan=False))
        return 0
    except KeyboardInterrupt:
        print('{"error":{"code":"interrupted"}}', file=sys.stderr)
        return 130
    except WorkflowError as error:
        print(json.dumps({"error": {"code": redact(str(error))}}), file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError):
        # Never expose CLI stdout/stderr, filesystem details or a traceback.
        print('{"error":{"code":"local_workflow_failed"}}', file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
