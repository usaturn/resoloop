"""Exercise invalid process output without real Blender, a GLB or credentials.

Catch cleanup overriding fixed errors, ignoring malformed stderr, orphan writes,
failed conversion publication and breaking UTF-8/Unicode path success.
"""

import hashlib
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
ENTRY = Path(os.environ.get(
    "RESOLOOP_MESHY_ENTRY",
    ROOT / "skills/codex/meshy-resoloop/scripts/meshy.py",
)).expanduser().resolve()

FAKE = r'''#!__PYTHON__
import json, os, pathlib, subprocess, sys, time
phase = 'conversion' if sys.argv[2] == 'run' else 'export'
if os.environ.get('STAGE_OUTPUT') == '1':
    if phase == 'conversion':
        def argument(flag):
            return sys.argv[sys.argv.index('--arg=--' + flag) + 1].removeprefix('--arg=')
        pathlib.Path(argument('output')).write_bytes(b'partial blend')
        pathlib.Path(argument('report')).write_text(json.dumps({'state': 'completed'}))
    else:
        bundle = pathlib.Path(sys.argv[sys.argv.index('--output') + 1])
        bundle.mkdir()
        (bundle / 'partial.mesh.json').write_text('{}')
if os.environ.get('SPAWN_DESCENDANT') == '1':
    code = ("import pathlib,time;pathlib.Path(" + repr(os.environ['READY']) +
            ").write_text('ready');time.sleep(0.8);pathlib.Path(" +
            repr(os.environ['MARKER']) + ").write_text('orphan')")
    subprocess.Popen([sys.executable, '-c', code], stdin=subprocess.DEVNULL,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + 2
    while not pathlib.Path(os.environ['READY']).exists():
        if time.monotonic() > deadline:
            sys.exit(91)
        time.sleep(0.01)
print(json.dumps({'ok': True, 'data': {'path': '世界/モデル.blend'}}, ensure_ascii=False), flush=True)
if os.environ.get('INVALID_PHASE', phase) == phase:
    stream = os.environ.get('INVALID_STREAM')
    if stream:
        os.write(1 if stream == 'stdout' else 2, b'\xff')
'''


@pytest.fixture
def boundary(tmp_path):
    project = tmp_path / '世界 with spaces'
    project.mkdir()
    (project / '.resoloop.json').write_text('{}')
    bin_dir = tmp_path / 'bin'
    bin_dir.mkdir()
    tool = bin_dir / 'resoloop'
    tool.write_text(FAKE.replace('__PYTHON__', sys.executable))
    tool.chmod(0o755)
    # Explicit minimal environment: never expose inherited credentials in failures.
    env = {'PATH': str(bin_dir), 'HOME': str(tmp_path), 'LANG': 'C.UTF-8',
           'PYTHONIOENCODING': 'utf-8', 'PYTHONDONTWRITEBYTECODE': '1',
           'READY': str(tmp_path / 'ready'), 'MARKER': str(tmp_path / 'late-write')}
    return project, env


def execute(boundary, body, *args):
    bootstrap = f'import sys; sys.path.insert(0, {str(ENTRY.parent)!r});\n' + body
    return subprocess.run([sys.executable, '-c', bootstrap, *args], env=boundary[1],
                          text=True, capture_output=True, timeout=6)


# Catch retrying text-mode communicate in finally and escaping _probe's None API.
@pytest.mark.parametrize('stream', ['stdout', 'stderr'])
def test_invalid_probe_output_returns_none_and_stops_exited_parent_descendants(boundary, stream):
    boundary[1].update(INVALID_STREAM=stream, SPAWN_DESCENDANT='1')
    proc = execute(boundary, '''import json
from meshy_doctor import _probe
print(json.dumps(_probe(['resoloop', 'blender', 'run'])))
''')
    assert proc.returncode == 0, proc.stderr
    assert json.loads(proc.stdout) is None
    assert Path(boundary[1]['READY']).is_file()
    time.sleep(0.9)
    assert not Path(boundary[1]['MARKER']).exists(), 'invalid-output probe descendant survived'


# Catch raw UnicodeError escaping either phase, and killing only the exited parent.
@pytest.mark.parametrize('stream', ['stdout', 'stderr'])
@pytest.mark.parametrize('phase,args', [('conversion', ['run', 'unused.py']),
                                        ('export', ['export', 'unused.blend'])])
def test_invalid_resoloop_output_keeps_phase_error_and_stops_descendants(boundary, stream, phase, args):
    boundary[1].update(INVALID_STREAM=stream, SPAWN_DESCENDANT='1')
    proc = execute(boundary, '''from pathlib import Path
from meshy_conversion import run_resoloop
from meshy_workflow import WorkflowError
try:
    run_resoloop(Path(sys.argv[1]), sys.argv[3:], sys.argv[2])
except WorkflowError as error:
    print(str(error))
    raise SystemExit(1)
''', str(boundary[0]), phase, *args)
    assert proc.returncode == 1
    assert proc.stdout.strip() == 'resoloop_' + phase + '_failed'
    assert not proc.stderr
    assert Path(boundary[1]['READY']).is_file()
    time.sleep(0.9)
    assert not Path(boundary[1]['MARKER']).exists(), 'invalid-output exporter descendant survived'


# A subprocess failure must not publish staged output or mutate the manifest.
@pytest.mark.parametrize('stream', ['stdout', 'stderr'])
@pytest.mark.parametrize('phase', ['conversion', 'export'])
def test_invalid_output_discards_partial_conversion_without_real_glb(boundary, stream, phase):
    project, env = boundary
    directory = project / 'content/generated/meshy/asset'
    source = directory / 'source'
    source.mkdir(parents=True)
    glb = source / 'fixture.glb'
    glb.write_bytes(b'not a real GLB; only the process boundary is exercised')
    manifest = directory / 'operation.json'
    manifest.write_text(json.dumps({
        'schema_version': 1, 'operation_id': '3439a405-277e-458a-8864-6c7d8e775c77',
        'kind': 'text', 'resource': 'text-to-3d', 'request': {}, 'stage': 'downloaded',
        'task': {'task_id': 'offline-output-regression', 'status': 'SUCCEEDED'},
        'downloads': {'state': 'completed', 'files': [{
            'key': 'model.glb', 'path': str(glb), 'status': 'written',
            'bytes': glb.stat().st_size, 'sha256': hashlib.sha256(glb.read_bytes()).hexdigest(),
        }]},
    }))
    before = manifest.read_bytes()
    env.update(INVALID_STREAM=stream, INVALID_PHASE=phase, STAGE_OUTPUT='1')
    proc = subprocess.run([
        sys.executable, str(ENTRY), '--project', str(project), 'convert',
        '--operation', 'asset', '--height', '2', '--parent', 'id:offline-unobserved',
    ], env=env, text=True, capture_output=True, timeout=6)
    assert proc.returncode == 1
    assert json.loads(proc.stderr)['error']['code'] == 'resoloop_' + phase + '_failed'
    assert not proc.stdout
    assert manifest.read_bytes() == before
    assert not (directory / 'converted').exists()
    assert not list(directory.glob('.conversion-*'))


# Switching capture to bytes must still accept UTF-8 stdout and Unicode paths.
def test_valid_utf8_probe_preserves_unicode_stdout(boundary):
    proc = execute(boundary, '''from meshy_doctor import _probe
print(_probe(['resoloop', 'blender', 'run']), end='')
''')
    assert proc.returncode == 0, proc.stderr
    assert json.loads(proc.stdout)['data']['path'] == '世界/モデル.blend'


def test_valid_utf8_resoloop_output_accepts_unicode_project(boundary):
    proc = execute(boundary, '''from pathlib import Path
from meshy_conversion import run_resoloop
run_resoloop(Path(sys.argv[1]), ['run', 'unused.py'], 'conversion')
print('accepted')
''', str(boundary[0]))
    assert proc.returncode == 0, proc.stderr
    assert proc.stdout.strip() == 'accepted'
