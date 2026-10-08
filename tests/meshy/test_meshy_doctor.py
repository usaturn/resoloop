"""Execute diagnostic and pre-submit boundaries with local fake executables.

Catch leaked probe secrets, API calls during diagnosis, writes, unbounded hangs,
unsupported-platform imports and compatibility failures recorded as submissions.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
ENTRY = Path(os.environ.get(
    "RESOLOOP_MESHY_ENTRY",
    ROOT / "skills/codex/meshy-resoloop/scripts/meshy.py",
)).expanduser().resolve()
KEY = "doctor_secret_sentinel_123456"

FAKE = r'''#!__PYTHON__
import json, os, pathlib, sys, time
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
with open(os.environ['PROBE_CALLS'], 'a') as out:
    out.write(json.dumps({'tool': name, 'args': args, 'env': dict(os.environ)}) + '\n')
if os.environ.get('DESCENDANT_TOOL') == name:
    import subprocess
    subprocess.Popen([sys.executable, '-c',
        "import time,pathlib;time.sleep(0.8);pathlib.Path(" + repr(os.environ['LATE_MARKER']) + ").write_text('orphan')"])
    sys.exit(0)
if os.environ.get('SLOW_TOOL') == name:
    time.sleep(5)
if os.environ.get('INVALID_TOOL') == name:
    os.write(1 if os.environ['INVALID_STREAM'] == 'stdout' else 2, b'\xff')
if os.environ.get('BAD_TOOL') == name:
    print('unexpected output doctor_secret_sentinel_123456')
    print('doctor_secret_sentinel_123456', file=sys.stderr)
    sys.exit(1)
if name == 'node' and args == ['--version']:
    print(os.environ.get('NODE_VERSION', 'v24.0.0'))
elif name == 'meshy' and args == ['--no-update-check', '--version']:
    print(os.environ.get('FAKE_MESHY_VERSION', '0.4.0'))
elif name == 'uv' and args == ['--version']:
    print('uv 0.9.0')
elif name == 'resoloop' and args == ['--version']:
    print('resoloop 0.1.0-preview.17')
elif name == 'resoloop' and args == ['blender', 'export', '--help']:
    print('resoloop blender export --preserve-hierarchy --pack-pbr')
elif name == 'resoloop' and args == ['blender', 'find', '--json']:
    print(json.dumps({'ok': True, 'data': {'executable': str(pathlib.Path(sys.argv[0]).with_name('blender')), 'version': 'Blender 5.0.1'}}))
elif name == 'blender' and '--python-expr' in args:
    expression = args[args.index('--python-expr')+1]
    # Execute the actual expression, replacing only the unavailable Blender modules.
    import types
    sys.modules['bpy'] = types.ModuleType('bpy')
    if os.environ.get('NO_NUMPY') == '1':
        sys.modules['numpy'] = None  # Block even an importable host NumPy.
    else:
        sys.modules['numpy'] = types.ModuleType('numpy')
    exec(expression)
else:
    print('forbidden API or unexpected probe', file=sys.stderr)
    sys.exit(90)
'''


@pytest.fixture
def local(tmp_path):
    project = tmp_path / 'world with spaces'
    project.mkdir()
    (project / '.resoloop.json').write_text('{}')
    (project / 'user-data.txt').write_text('retain')
    bin_dir = tmp_path / 'bin'
    bin_dir.mkdir()
    for name in ('node', 'meshy', 'uv', 'resoloop', 'blender'):
        tool = bin_dir / name
        tool.write_text(FAKE.replace('__PYTHON__', sys.executable))
        tool.chmod(0o755)
    env = {k: v for k, v in os.environ.items()
           if k in ('HOME', 'LANG', 'SYSTEMROOT', 'LD_LIBRARY_PATH')}
    env.update(PATH=str(bin_dir), PROBE_CALLS=str(tmp_path / 'calls.jsonl'),
               PYTHONDONTWRITEBYTECODE='1')
    return project, bin_dir, env


def invoke(local, *args, platform=None, block_posix=False):
    project, _, env = local
    bootstrap = f'import sys, shutil; sys.path.insert(0, {str(ENTRY.parent)!r}); '
    if platform:
        bootstrap += f'sys.platform={platform!r}; '
    if block_posix:
        bootstrap += '''
import builtins
original = builtins.__import__
def guarded(name, *args, **kwargs):
    if name in ('fcntl', 'meshy_workflow', 'meshy_conversion'):
        raise AssertionError('eager POSIX import')
    return original(name, *args, **kwargs)
builtins.__import__ = guarded
'''
    if args and args[0] != '--help' and not platform:
        # Do not turn the RED phase into an import error: missing doctor is exposed
        # through the CLI's missing command until the new module exists.
        bootstrap += "\nimport importlib.util\nif importlib.util.find_spec('meshy_doctor'):\n    import meshy_doctor\n    meshy_doctor.PROBE_TIMEOUT_SECONDS=0.25\n"
    bootstrap += '\nimport meshy; raise SystemExit(meshy.main())'
    return subprocess.run([sys.executable, '-c', bootstrap, '--project', str(project), *args],
                          env=env, text=True, capture_output=True, timeout=6)


def calls(local):
    path = Path(local[2]['PROBE_CALLS'])
    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def snapshot(project):
    return {str(p.relative_to(project)): (
                p.read_bytes() if p.is_file() else None, p.stat().st_mtime_ns)
            for p in [project, *project.rglob('*')]}


# Missing keys must not block local diagnosis; all probes must stay offline/read-only.
def test_keyless_doctor_checks_local_dependencies_without_writing(local):
    before = snapshot(local[0])
    proc = invoke(local, 'doctor')
    assert proc.returncode == 0, proc.stdout + proc.stderr
    result = json.loads(proc.stdout)
    assert result['ok'] is True and result['key_present'] is False
    assert all(check['ok'] for check in result['checks'])
    assert {'platform', 'python', 'uv', 'node', 'meshy', 'resoloop',
            'exporter', 'blender', 'bpy_numpy', 'resources', 'project'} <= {
                check['name'] for check in result['checks']}
    assert snapshot(local[0]) == before
    assert calls(local)
    assert not any(set(c['args']) & {'login', 'status', 'list', 'balance', 'create'} for c in calls(local))


@pytest.mark.parametrize('variable,value,check', [
    ('NODE_VERSION', 'v22.11.0', 'node'),
    ('NODE_VERSION', 'v22.12.0', None),
    ('NODE_VERSION', 'v23.0.0', None),
    ('FAKE_MESHY_VERSION', '0.4.1', 'meshy'),
    ('FAKE_MESHY_VERSION', '0.4.0-beta.1', 'meshy'),
    ('FAKE_MESHY_VERSION', 'garbage', 'meshy'),
    ('BAD_TOOL', 'resoloop', 'resoloop'),
    ('SLOW_TOOL', 'node', 'node'),
    ('NO_NUMPY', '1', 'bpy_numpy'),
])
def test_diagnostic_exit_reflects_dependency_failure(local, variable, value, check):
    local[2][variable] = value
    before = snapshot(local[0])
    proc = invoke(local, 'doctor')
    result = json.loads(proc.stdout)
    assert proc.returncode == (1 if check else 0)
    assert result['ok'] is (check is None)
    if check:
        failed = next(c for c in result['checks'] if c['name'] == check)
        assert failed['ok'] is False and failed['repair']
    assert KEY not in proc.stdout + proc.stderr
    assert snapshot(local[0]) == before


# Catch cleanup decoding invalid bytes a second time and hiding named repairs.
@pytest.mark.parametrize('stream', ['stdout', 'stderr'])
@pytest.mark.parametrize('tool,check', [('node', 'node'), ('blender', 'bpy_numpy')])
def test_invalid_probe_output_keeps_diagnostic_checks(local, stream, tool, check):
    local[2].update(INVALID_TOOL=tool, INVALID_STREAM=stream)
    before = snapshot(local[0])
    proc = invoke(local, 'doctor')
    assert proc.returncode == 1
    assert proc.stdout, proc.stderr
    result = json.loads(proc.stdout)
    failed = next(c for c in result['checks'] if c['name'] == check)
    assert result['ok'] is False
    assert failed['ok'] is False and failed['repair']
    assert not proc.stderr
    assert snapshot(local[0]) == before


# A real/importable host NumPy must not mask the fake's missing dependency.
def test_missing_numpy_check_cannot_use_host_numpy(local):
    (local[1] / 'numpy.py').write_text('AVAILABLE = True\n')
    local[2]['NO_NUMPY'] = '1'
    proc = invoke(local, 'doctor')
    result = json.loads(proc.stdout)
    assert proc.returncode == 1
    failed = next(c for c in result['checks'] if c['name'] == 'bpy_numpy')
    assert failed['ok'] is False and failed['repair']


@pytest.mark.parametrize('tool', ['node', 'meshy', 'uv', 'resoloop'])
def test_missing_dependency_fails_with_named_check(local, tool):
    (local[1] / tool).unlink()
    proc = invoke(local, 'doctor')
    result = json.loads(proc.stdout)
    assert proc.returncode == 1
    assert next(c for c in result['checks'] if c['name'] == tool)['ok'] is False


# Passing ambient overrides into version probes can load hostile Node code or leak credentials.
def test_doctor_strips_secrets_and_node_overrides_from_every_probe(local):
    local[2].update(MESHY_API_KEY=KEY, MESHY_OTHER=KEY, NODE_OPTIONS=KEY,
                    NODE_DEBUG=KEY, HTTPS_PROXY=KEY)
    proc = invoke(local, 'doctor')
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert json.loads(proc.stdout)['key_present'] is True
    assert KEY not in proc.stdout + proc.stderr
    for call in calls(local):
        assert not any(k.startswith('MESHY_') or k in ('NODE_OPTIONS', 'NODE_DEBUG', 'HTTPS_PROXY')
                       for k in call['env'])
        assert KEY not in json.dumps(call)


@pytest.mark.parametrize('platform', ['win32', 'darwin'])
def test_nonlinux_help_succeeds_without_importing_posix_modules(local, platform):
    proc = invoke(local, '--help', platform=platform, block_posix=True)
    assert proc.returncode == 0, proc.stdout + proc.stderr


@pytest.mark.parametrize('platform', ['win32', 'darwin'])
def test_nonlinux_operations_stop_with_actionable_named_error(local, platform):
    before = snapshot(local[0])
    proc = invoke(local, 'plan', '--operation', 'asset', '--kind', 'text', '--prompt', 'stone',
                  platform=platform, block_posix=True)
    assert proc.returncode == 1
    assert json.loads(proc.stderr)['error']['code'] == 'unsupported_platform_use_linux_or_wsl2'
    assert not calls(local) and snapshot(local[0]) == before


# A mismatched CLI must fail before child_env/journal or stage=submitting.
@pytest.mark.parametrize('variable,value', [('NODE_VERSION', 'v22.11.0'),
                                           ('FAKE_MESHY_VERSION', '0.5.0'),
                                           ('SLOW_TOOL', 'meshy')])
def test_incompatible_submit_never_records_or_sends_submission(local, variable, value):
    local[2]['MESHY_API_KEY'] = KEY
    assert invoke(local, 'plan', '--operation', 'asset', '--kind', 'text', '--prompt', 'stone').returncode == 0
    before = snapshot(local[0])
    local[2][variable] = value
    proc = invoke(local, 'submit', '--operation', 'asset', '--confirm-paid')
    assert proc.returncode == 1
    assert json.loads(proc.stderr)['error']['code'].startswith('cli_compatibility_')
    assert snapshot(local[0]) == before
    assert not (local[0] / '.resoloop/meshy-cli').exists()
    assert not any('create' in c['args'] for c in calls(local))
    assert KEY not in proc.stdout + proc.stderr


# Invalid bytes must not turn compatibility failures into local_workflow_failed.
@pytest.mark.parametrize('stream', ['stdout', 'stderr'])
@pytest.mark.parametrize('tool,code', [
    ('node', 'cli_compatibility_node_requires_22_12_0'),
    ('meshy', 'cli_compatibility_meshy_requires_0_4_0'),
])
def test_invalid_version_output_stops_submit_with_named_error(local, stream, tool, code):
    local[2]['MESHY_API_KEY'] = KEY
    assert invoke(local, 'plan', '--operation', 'asset', '--kind', 'text', '--prompt', 'stone').returncode == 0
    before = snapshot(local[0])
    local[2].update(INVALID_TOOL=tool, INVALID_STREAM=stream)
    proc = invoke(local, 'submit', '--operation', 'asset', '--confirm-paid')
    assert proc.returncode == 1
    assert json.loads(proc.stderr)['error']['code'] == code
    assert snapshot(local[0]) == before
    assert not (local[0] / '.resoloop/meshy-cli').exists()
    assert not any('create' in c['args'] for c in calls(local))
    assert KEY not in proc.stdout + proc.stderr


# Catch a version tool exiting while a descendant keeps its output pipe open.
def test_doctor_timeout_stops_descendants_even_after_parent_exits(local):
    import time
    marker = local[0].parent / 'late-write'
    local[2].update(DESCENDANT_TOOL='node', LATE_MARKER=str(marker))
    proc = invoke(local, 'doctor')
    assert proc.returncode == 1
    time.sleep(0.9)
    assert not marker.exists(), 'timed-out probe descendant survived'
