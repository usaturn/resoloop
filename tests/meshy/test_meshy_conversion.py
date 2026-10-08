"""Real offline Blender/export tests, plus publication and write-boundary failures.

Mutations caught: joining objects, losing textures/channels, wrong transforms,
publishing a failed export, trusting stale hashes or paths, overwriting outputs.
"""

import fcntl
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
ENTRY = Path(os.environ.get(
    "RESOLOOP_MESHY_ENTRY",
    ROOT / "skills/codex/meshy-resoloop/scripts/meshy.py",
)).expanduser().resolve()
FIXTURE = ROOT / "tests/fixtures/meshy/create_fixture.py"
PARENT = 'path:["Root","VerifiedFixtureParent"]'


def run(argv, *, env=None, cwd=None):
    return subprocess.run(
        argv, env=env, cwd=cwd, text=True, capture_output=True, timeout=90, check=False
    )


@pytest.fixture(scope="module")
def tools():
    resoloop = shutil.which("resoloop")
    if not resoloop:
        pytest.skip("ResoLoop CLI unavailable; real offline exporter required")
    found = run([resoloop, "blender", "find", "--json"])
    if found.returncode:
        pytest.skip("ResoLoop cannot find Blender/bpy: " + found.stdout)
    return resoloop


@pytest.fixture(scope="module")
def glbs(tmp_path_factory, tools):
    directory = tmp_path_factory.mktemp("real-glbs")
    result = {}
    for case in (
        "opaque",
        "transparency",
        "transmission",
        "animation",
        "tilted_tetrahedron",
        "one_sided",
        "mixed_culling",
        "implicit_material",
        "mixed_default_material",
    ):
        path = directory / (case + ".glb")
        proc = run(
            [
                tools,
                "blender",
                "run",
                str(FIXTURE),
                "--arg=--output",
                "--arg=" + str(path),
                "--arg=--case",
                "--arg=" + case,
                "--json",
            ]
        )
        assert proc.returncode == 0, proc.stdout + proc.stderr
        assert path.read_bytes()[:4] == b"glTF"
        result[case] = path
    return result


@pytest.fixture
def world(tmp_path, glbs):
    project = tmp_path / "world"
    project.mkdir()
    (project / ".resoloop.json").write_text("{}")
    operation = project / "content/generated/meshy/asset"
    source = operation / "source"
    source.mkdir(parents=True)
    # Deliberately not generated.glb/model.glb: consume the download manifest.
    path = source / "arbitrary-download-name.glb"
    shutil.copyfile(glbs["opaque"], path)
    manifest = {
        "schema_version": 1,
        "operation_id": str(uuid.uuid4()),
        "kind": "image",
        "resource": "image-to-3d",
        "request": {},
        "stage": "downloaded",
        "task": {"task_id": "offline-fixture", "status": "SUCCEEDED"},
        "downloads": {
            "state": "completed",
            "files": [
                {
                    "key": "model.glb",
                    "path": str(path),
                    "status": "written",
                    "bytes": path.stat().st_size,
                    "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                }
            ],
        },
    }
    manifest_path = operation / "operation.json"
    manifest_path.write_text(json.dumps(manifest))
    return project, operation, path, manifest_path


def convert(world, *extra, env=None):
    project = world[0]
    child_env = {k: v for k, v in os.environ.items() if not k.startswith("MESHY_")}
    if env:
        child_env.update(env)
    return run(
        [
            sys.executable,
            str(ENTRY),
            "--project",
            str(project),
            "convert",
            "--operation",
            "asset",
            "--height",
            "2",
            "--yaw",
            "90",
            "--origin",
            "ground",
            "--parent",
            PARENT,
            "--name",
            "OfflineFixture",
            *extra,
        ],
        env=child_env,
    )


def assert_unpublished(world):
    assert not (world[1] / "converted").exists()
    assert "conversion" not in json.loads(world[3].read_text())
    assert not list(world[1].glob(".conversion-*"))


# Catch transform/channel/texture loss in the actual saved blend and exported bundle.
@pytest.mark.parametrize("origin", ["ground", "center"])
def test_real_conversion_preserves_objects_textures_and_normalizes_bounds(
    world, tools, tmp_path, origin
):
    before = world[2].read_bytes()
    proc = convert(world, "--origin", origin, "--allow-culling-change")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    result = json.loads(proc.stdout)
    assert world[2].read_bytes() == before
    conversion = json.loads(world[3].read_text())["conversion"]
    assert conversion == result
    blend = Path(conversion["blend"])
    report = json.loads(Path(conversion["report"]).read_text())
    bundle = Path(conversion["bundle"])
    assert blend.is_file() and blend.is_relative_to(world[1] / "converted")
    assert report["mesh_count"] == 2
    assert report["triangles"] == 24
    assert report["materials"] == 1
    assert report["bounds"]["height"] == pytest.approx(2)
    assert report["bounds"]["min"][2] == pytest.approx(0 if origin == "ground" else -1)
    exported = json.loads((bundle / "report.json").read_text())
    assert exported["preserveHierarchy"] is True
    assert exported["triangles"] == 24
    assert {m["name"] for m in exported["meshes"]} == {"LowerPart", "UpperPart"}
    assert len(exported["textures"]) == 2
    assert all((bundle / t["file"]).is_file() for t in exported["textures"])
    assert all(t["width"] == t["height"] == 2 for t in exported["textures"])
    apply = json.loads((bundle / "model.apply.json").read_text())
    assert apply["slot"]["parent"] == PARENT
    assert all((bundle / a["source"]).is_file() for a in apply["assets"].values())
    assert exported["source"] == str(blend)
    assert exported["outputDirectory"] == str(bundle)
    # Reopen, not just inspect the converter's own report: pack must survive saving.
    probe = tmp_path / "probe.py"
    probe.write_text("""import bpy, json, sys
from pathlib import Path
objects = [o for o in bpy.context.scene.objects if o.type == 'MESH']
points = [o.matrix_world @ vertex.co for o in objects for vertex in o.data.vertices]
sh = bpy.data.materials['FixtureMaterial'].node_tree.nodes.get('Principled BSDF')
albedo = sh.inputs['Base Color'].links[0].from_node.image
channels = {}
for name in ('Metallic', 'Roughness'):
    node = sh.inputs[name].links[0].from_node
    channels[name] = {'type': node.type, 'space': node.image.colorspace_settings.name,
                      'pixels': list(node.image.pixels), 'packed': bool(node.image.packed_file)}
original_images = [{'name':i.name, 'packed':bool(i.packed_file), 'size':list(i.size)} for i in bpy.data.images if i.type == 'IMAGE']
pbr = bpy.data.images.load(str(next(Path(sys.argv[-2]).glob('pbr_*.png'))))
pbr.colorspace_settings.name = 'Non-Color'
Path(sys.argv[-1]).write_text(json.dumps({'channels': channels,
    'albedo': {'space':albedo.colorspace_settings.name, 'pixels':list(albedo.pixels)},
    'packed_pbr': list(pbr.pixels),
    'parents': {o.name:o.parent.name for o in objects},
    'centers': {o.name:list(o.matrix_world.translation) for o in objects},
    'images': original_images,
    'min': [min(p[i] for p in points) for i in range(3)],
    'max': [max(p[i] for p in points) for i in range(3)]}))
""")
    inspection = tmp_path / "inspection.json"
    check = run(
        [
            tools,
            "blender",
            "run",
            str(probe),
            "--blend",
            str(blend),
            "--arg=" + str(bundle),
            "--arg=" + str(inspection),
            "--json",
        ]
    )
    assert check.returncode == 0, check.stdout + check.stderr
    saved = json.loads(inspection.read_text())
    assert saved["parents"] == {
        "LowerPart": "FixtureHierarchy",
        "UpperPart": "FixtureHierarchy",
    }
    assert saved["centers"]["LowerPart"][:2] == pytest.approx([0, -0.5], abs=1e-6)
    assert saved["centers"]["UpperPart"][:2] == pytest.approx([0, 0.5], abs=1e-6)
    assert saved["max"][2] - saved["min"][2] == pytest.approx(2)
    assert saved["albedo"]["space"] == "sRGB"
    assert saved["albedo"]["pixels"] == pytest.approx(
        [1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 0, 1], abs=1 / 255
    )
    assert saved["packed_pbr"] == pytest.approx([0.75, 1, 0, 0.75] * 4, abs=1 / 255)
    for name, value in (("Metallic", 0.75), ("Roughness", 0.25)):
        channel = saved["channels"][name]
        assert channel["type"] == "TEX_IMAGE" and channel["space"] == "Non-Color"
        assert channel["packed"]
        assert channel["pixels"][:4] == pytest.approx(
            [value, value, value, 1], abs=1 / 255
        )
    assert all(i["packed"] and i["size"] == [2, 2] for i in saved["images"])
    # A second conversion may not destroy a successful editable source/bundle.
    second = convert(world)
    assert second.returncode != 0
    assert blend.is_file()
    assert json.loads(world[3].read_text())["conversion"] == conversion


# Catch fictitious transformed box corners: a tilted tetrahedron must not float
# or be undersized. Independently measure real saved vertices, not report helpers.
@pytest.mark.parametrize("origin", ["ground", "center"])
def test_tilted_irregular_geometry_uses_actual_world_vertices(
    world, glbs, tools, tmp_path, origin
):
    replace_glb(world, glbs["tilted_tetrahedron"])
    proc = convert(world, "--yaw", "37", "--origin", origin, "--allow-culling-change")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    conversion = json.loads(proc.stdout)
    probe = tmp_path / "vertex_probe.py"
    probe.write_text("""import bpy, json, sys
from pathlib import Path
vertices = [list(obj.matrix_world @ v.co) for obj in bpy.context.scene.objects
            if obj.type == 'MESH' for v in obj.data.vertices]
Path(sys.argv[-1]).write_text(json.dumps(vertices))
""")
    inspection = tmp_path / "vertices.json"
    check = run(
        [
            tools,
            "blender",
            "run",
            str(probe),
            "--blend",
            conversion["blend"],
            "--arg=" + str(inspection),
            "--json",
        ]
    )
    assert check.returncode == 0, check.stdout + check.stderr
    vertices = json.loads(inspection.read_text())
    minimum = [min(v[i] for v in vertices) for i in range(3)]
    maximum = [max(v[i] for v in vertices) for i in range(3)]
    assert maximum[2] - minimum[2] == pytest.approx(2, abs=1e-6)
    assert minimum[2] == pytest.approx(0 if origin == "ground" else -1, abs=1e-6)
    assert maximum[2] == pytest.approx(2 if origin == "ground" else 1, abs=1e-6)
    assert [(a + b) / 2 for a, b in zip(minimum[:2], maximum[:2])] == pytest.approx(
        [0, 0], abs=1e-6
    )
    report = json.loads(Path(conversion["report"]).read_text())
    assert report["bounds"]["min"] == pytest.approx(minimum, abs=1e-6)
    assert report["bounds"]["max"] == pytest.approx(maximum, abs=1e-6)
    assert report["triangles"] == 4


def replace_glb(world, fixture):
    shutil.copyfile(fixture, world[2])
    manifest = json.loads(world[3].read_text())
    manifest["downloads"]["files"][0].update(
        bytes=world[2].stat().st_size,
        sha256=hashlib.sha256(world[2].read_bytes()).hexdigest(),
    )
    world[3].write_text(json.dumps(manifest))


# Catch silently losing either culling mode (including glTF's omitted/false
# default). Real GLBs, named errors and no published success are required.
@pytest.mark.parametrize(
    "case,label",
    [
        ("one_sided", "FixtureMaterial"),
        ("opaque", "FixtureMaterial"),
        ("implicit_material", "Default glTF material"),
    ],
)
def test_culling_requires_explicit_consent(world, glbs, case, label):
    replace_glb(world, glbs[case])
    proc = convert(world)
    assert proc.returncode != 0
    assert f"blender_rejected:{label}:culling_preservation_unsupported" in proc.stderr
    assert_unpublished(world)


# Catch bypassing the CLI safety default by calling the public function directly.
def test_conversion_function_defaults_to_culling_rejection(world):
    bootstrap = f"""import sys
from pathlib import Path
sys.path.insert(0, {str(ENTRY.parent)!r})
from meshy_conversion import convert_operation
from meshy_workflow import WorkflowError
try:
    convert_operation(Path(sys.argv[1]), 'asset', height=2, yaw=0, origin='ground',
                      parent={PARENT!r}, model_name='OfflineFixture')
except WorkflowError as error:
    print(str(error), file=sys.stderr)
    raise SystemExit(1)
"""
    proc = run([sys.executable, "-c", bootstrap, str(world[0])])
    assert proc.returncode != 0
    assert (
        "blender_rejected:FixtureMaterial:culling_preservation_unsupported"
        in proc.stderr
    )
    assert_unpublished(world)


# Catch an acknowledgement that succeeds without exposing every original mode
# and the specific unsupported preservation. No FrooxEngine enum is assumed.
@pytest.mark.parametrize(
    "case,originals",
    [
        ("one_sided", {"FixtureMaterial": False}),
        ("opaque", {"FixtureMaterial": True}),
        ("mixed_culling", {"FixtureMaterial": True, "OneSidedMaterial": False}),
        ("implicit_material", {"Default glTF material": False}),
    ],
)
def test_culling_consent_reports_each_original_material(world, glbs, case, originals):
    replace_glb(world, glbs[case])
    before = world[2].read_bytes()
    proc = convert(world, "--allow-culling-change")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    conversion = json.loads(proc.stdout)
    assert conversion["allow_culling_change"] is True
    assert json.loads(world[3].read_text())["conversion"] == conversion
    report = json.loads(Path(conversion["report"]).read_text())
    assert report["allow_culling_change"] is True
    assert {
        m["name"]: m["doubleSided"] for m in report["material_culling"]
    } == originals
    assert {w["name"]: w["doubleSided"] for w in report["warnings"]} == originals
    assert all(
        w["reason"] == "culling_preservation_unsupported" for w in report["warnings"]
    )
    assert all(w["message"] for w in report["warnings"])
    assert world[2].read_bytes() == before


# Catch letting culling consent also change glTF's implicit white/metallic/rough
# appearance to the exporter's gray nonmetal fallback, or overwrite authored
# faces when filling empty slots. Values come from glTF 2.0's Default Material
# and material.pbrMetallicRoughness schemas, not Blender defaults.
@pytest.mark.parametrize("case", ["implicit_material", "mixed_default_material"])
def test_implicit_material_preserves_appearance_in_bundle_and_saved_blend(
    world, glbs, tools, tmp_path, case
):
    replace_glb(world, glbs[case])
    before = world[2].read_bytes()
    proc = convert(world, "--allow-culling-change")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    conversion = json.loads(proc.stdout)
    bundle = Path(conversion["bundle"])
    document = json.loads((bundle / "model.apply.json").read_text())

    def components(slot):
        yield from slot.get("components", [])
        for child in slot.get("children", []):
            yield from components(child)

    exported = list(components(document))
    materials = {
        "$component:" + c["key"]: c["fields"]
        for c in exported
        if c["type"] == "FrooxEngine.PBS_Metallic"
    }
    expected_materials = 1 if case == "implicit_material" else 2
    assert len(materials) == expected_materials
    defaults = {
        ref: fields
        for ref, fields in materials.items()
        if "AlbedoTexture" not in fields
    }
    assert len(defaults) == 1
    default_ref, fields = next(iter(defaults.items()))
    assert fields["AlbedoColor"] == pytest.approx([1, 1, 1, 1])
    assert fields["Metallic"] == pytest.approx(1)
    assert fields["Smoothness"] == pytest.approx(0)
    assert fields["EmissiveColor"] == pytest.approx([0, 0, 0, 1])
    assert not any(
        name in fields for name in ("NormalMap", "EmissiveMap", "MetallicMap")
    )
    report = json.loads(Path(conversion["report"]).read_text())
    assert report["materials"] == expected_materials
    assert {m["name"]: m["doubleSided"] for m in report["material_culling"]} == (
        {"Default glTF material": False}
        if case == "implicit_material"
        else {"FixtureMaterial": True, "Default glTF material": False}
    )
    # Check bindings per real submesh, not just an unused default provider.
    mesh_files = {
        "$component:" + c["key"]: document["assets"][
            c["fields"]["URL"].removeprefix("$asset:")
        ]["source"]
        for c in exported
        if c["type"] == "FrooxEngine.StaticMesh"
    }
    renderers = [c for c in exported if c["type"] == "FrooxEngine.MeshRenderer"]
    assert len(renderers) == 2
    for renderer in renderers:
        mesh = json.loads((bundle / mesh_files[renderer["fields"]["Mesh"]]).read_text())
        bindings = renderer["fields"]["Materials"]
        assert len(bindings) == len(mesh["submeshes"]) == expected_materials
        assert all(ref in materials for ref in bindings)
        triangles = {
            ref: len(section["vertexIndices"]) // 3
            for ref, section in zip(bindings, mesh["submeshes"], strict=True)
        }
        assert triangles[default_ref] == (12 if case == "implicit_material" else 6)
        if case == "mixed_default_material":
            authored_ref = next(ref for ref in bindings if ref != default_ref)
            assert triangles[authored_ref] == 6
            authored = materials[authored_ref]
            assert authored["AlbedoColor"] == pytest.approx([1, 1, 1, 1])
            assert authored["Metallic"] == authored["Smoothness"] == 1
            assert "AlbedoTexture" in authored and "MetallicMap" in authored

    probe = tmp_path / "material_probe.py"
    probe.write_text("""import bpy, json, sys
from collections import Counter
from pathlib import Path
saved = {}
for obj in bpy.context.scene.objects:
    if obj.type != 'MESH':
        continue
    slots = list(obj.data.materials)
    shaders = {}
    for material in slots:
        if material is None:
            continue
        shader = material.node_tree.nodes.get('Principled BSDF')
        shaders[material.name] = {
            'color': list(shader.inputs['Base Color'].default_value),
            'metallic': shader.inputs['Metallic'].default_value,
            'roughness': shader.inputs['Roughness'].default_value,
            'emission': list(shader.inputs['Emission Color'].default_value),
            'alpha': shader.inputs['Alpha'].default_value,
            'culling': material.use_backface_culling}
    counts = Counter(slots[p.material_index].name if p.material_index < len(slots)
                     and slots[p.material_index] else None for p in obj.data.polygons)
    saved[obj.name] = {'slots': [m.name if m else None for m in slots],
                       'faces': dict(counts), 'shaders': shaders,
                       'bindings': [slots[p.material_index].name if p.material_index < len(slots)
                                    and slots[p.material_index] else None for p in obj.data.polygons]}
# Import the untouched GLB independently to check each face's original binding,
# including which side of the authored slot the importer placed the None slot.
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=sys.argv[-2])
original = {}
for obj in bpy.context.scene.objects:
    if obj.type != 'MESH':
        continue
    slots = list(obj.data.materials)
    original[obj.name] = {'slots': [m.name if m else None for m in slots],
        'bindings': [slots[p.material_index].name if p.material_index < len(slots)
                     and slots[p.material_index] else None for p in obj.data.polygons]}
Path(sys.argv[-1]).write_text(json.dumps({'saved': saved, 'original': original}))
""")
    inspection = tmp_path / "materials.json"
    check = run(
        [
            tools,
            "blender",
            "run",
            str(probe),
            "--blend",
            conversion["blend"],
            "--arg=" + str(world[2]),
            "--arg=" + str(inspection),
            "--json",
        ]
    )
    assert check.returncode == 0, check.stdout + check.stderr
    inspected = json.loads(inspection.read_text())
    saved = inspected["saved"]
    assert set(saved) == {"LowerPart", "UpperPart"}
    for name, obj in saved.items():
        assert len(obj["slots"]) == expected_materials
        assert None not in obj["slots"]
        assert set(obj["faces"]) == set(obj["slots"])
        assert sum(obj["faces"].values()) == 12
        default_name = next(name for name in obj["slots"] if name != "FixtureMaterial")
        original = inspected["original"][name]
        if case == "implicit_material":
            assert original["slots"] == []
        else:
            assert set(original["slots"]) == {None, "FixtureMaterial"}
        assert obj["bindings"] == [
            material if material is not None else default_name
            for material in original["bindings"]
        ]
        shader = obj["shaders"][default_name]
        assert shader["color"] == pytest.approx([1, 1, 1, 1])
        assert shader["metallic"] == shader["roughness"] == shader["alpha"] == 1
        assert shader["emission"][:3] == pytest.approx([0, 0, 0])
        assert shader["culling"] is True
        assert obj["faces"][default_name] == (12 if case == "implicit_material" else 6)
        if case == "mixed_default_material":
            assert obj["faces"]["FixtureMaterial"] == 6
    assert world[2].read_bytes() == before


# Catch silent static adaptation of alpha, specialized shaders or animated assets.
@pytest.mark.parametrize("allow_culling_change", [False, True])
@pytest.mark.parametrize(
    "case,label",
    [
        ("transparency", "FixtureMaterial"),
        ("transmission", "FixtureMaterial"),
        ("animation", "LowerPart"),
    ],
)
def test_unsupported_glb_fails_by_name_without_publication(
    world, glbs, case, label, allow_culling_change
):
    replace_glb(world, glbs[case])
    proc = convert(world, *(["--allow-culling-change"] if allow_culling_change else []))
    assert proc.returncode != 0
    assert label in proc.stderr, proc.stdout + proc.stderr
    if allow_culling_change:
        reason = {
            "transparency": "non_opaque_material",
            "transmission": "unsupported_input_Transmission Weight",
            "animation": "animation_unsupported",
        }[case]
        assert reason in proc.stderr
    assert_unpublished(world)


# Export failure really creates partial files: they must never become a usable bundle.
@pytest.mark.parametrize("mode", ["failure", "incomplete"])
def test_failed_export_is_not_published(world, tools, tmp_path, mode):
    bindir = tmp_path / "bin"
    bindir.mkdir()
    fake = bindir / "resoloop"
    fake.write_text(f"""#!{sys.executable}
import os, pathlib, sys
if sys.argv[1:3] == ['blender', 'run']:
    os.execv({tools!r}, [{tools!r}] + sys.argv[1:])
assert sys.argv[1:3] == ['blender', 'export']
p = pathlib.Path(sys.argv[sys.argv.index('--output') + 1])
p.mkdir()
(p / 'partial.mesh.json').write_text('{{}}')
print('{{"ok":{str(mode == "incomplete").lower()}}}')
sys.exit({0 if mode == "incomplete" else 1})
""")
    fake.chmod(0o755)
    proc = convert(
        world,
        "--allow-culling-change",
        env={"PATH": str(bindir) + os.pathsep + os.environ["PATH"]},
    )
    assert proc.returncode != 0
    assert (
        "conversion_outputs_missing"
        if mode == "incomplete"
        else "resoloop_export_failed"
    ) in proc.stderr
    assert_unpublished(world)


# Catch trusting edited manifests or escaping output/input boundaries before subprocesses.
@pytest.mark.parametrize(
    "bad",
    [
        "hash",
        "size",
        "partial",
        "ambiguous",
        "missing",
        "outside",
        "outside_project",
        "symlink",
        "output_symlink",
        "locked",
    ],
)
def test_conversion_rejects_unsafe_or_unready_inputs(world, tmp_path, bad):
    manifest = json.loads(world[3].read_text())
    entry = manifest["downloads"]["files"][0]
    lock = None
    if bad == "hash":
        entry["sha256"] = "0" * 64
    elif bad == "size":
        entry["bytes"] += 1
    elif bad == "partial":
        manifest["downloads"]["state"] = "partial"
    elif bad == "ambiguous":
        manifest["downloads"]["files"].append(dict(entry))
    elif bad == "missing":
        world[2].unlink()
    elif bad in ("outside", "outside_project", "symlink"):
        outside = (world[0] if bad == "outside" else tmp_path) / "outside.glb"
        shutil.copyfile(world[2], outside)
        if bad in ("outside", "outside_project"):
            entry["path"] = str(outside)
        else:
            world[2].unlink()
            world[2].symlink_to(outside)
    elif bad == "output_symlink":
        (world[1] / "converted").symlink_to(
            tmp_path / "elsewhere", target_is_directory=True
        )
    elif bad == "locked":
        lock = (world[1] / ".lock").open("w")
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    world[3].write_text(json.dumps(manifest))
    try:
        proc = convert(world, env={"PATH": "/nonexistent"})
        assert proc.returncode != 0
        expected = {
            "hash": "source_glb_changed",
            "size": "source_glb_changed",
            "partial": "completed_downloads_required",
            "ambiguous": "unique_glb_required",
            "missing": "source_glb_missing",
            "outside": "source_outside_operation",
            "outside_project": "path_outside_project",
            "symlink": "symlink_forbidden",
            "output_symlink": "symlink_forbidden",
            "locked": "operation_locked",
        }[bad]
        assert expected in proc.stderr, proc.stdout + proc.stderr
        assert "conversion" not in json.loads(world[3].read_text())
        assert not list(world[1].glob(".conversion-*"))
    finally:
        if lock:
            lock.close()


# Catch invalid normalization requests before spawning Blender (including API use).
@pytest.mark.parametrize(
    "extra",
    [
        ["--height", "0"],
        ["--height", "-2"],
        ["--height", "nan"],
        ["--yaw", "inf"],
        ["--parent", ""],
        ["--name", "../escape"],
    ],
)
def test_invalid_conversion_parameters_are_rejected(world, extra):
    proc = convert(world, *extra, env={"PATH": "/nonexistent"})
    assert proc.returncode != 0
    assert "invalid_conversion_parameters" in proc.stderr
    assert_unpublished(world)


# Catch a manifest write failing after replace: no success marker may survive rollback.
def test_failed_manifest_publication_restores_operation(world, tools):
    bootstrap = f"""import sys
sys.path.insert(0, {str(ENTRY.parent)!r})
import meshy_conversion, meshy
original = meshy_conversion.save_operation
def fail_after_replace(*args):
    original(*args)
    raise OSError('simulated directory fsync failure')
meshy_conversion.save_operation = fail_after_replace
raise SystemExit(meshy.main())
"""
    args = [
        "--project",
        str(world[0]),
        "convert",
        "--operation",
        "asset",
        "--height",
        "2",
        "--parent",
        PARENT,
        "--allow-culling-change",
    ]
    proc = run([sys.executable, "-c", bootstrap, *args])
    assert proc.returncode != 0
    assert "local_workflow_failed" in proc.stderr
    assert_unpublished(world)


# Catch timed-out Blender descendants writing into a removed staging directory.
def test_resoloop_timeout_stops_process_tree(world, tmp_path):
    bindir = tmp_path / "bin"
    bindir.mkdir()
    marker = tmp_path / "late-write"
    fake = bindir / "resoloop"
    fake.write_text(f"""#!{sys.executable}
import json, pathlib, subprocess, sys, time
if sys.argv[2] == 'run':
    print(json.dumps({{'ok':True}}))
else:
    subprocess.Popen([sys.executable, '-c',
        "import time,pathlib;time.sleep(0.5);pathlib.Path({str(marker)!r}).write_text('orphan')"])
    time.sleep(1)
    print(json.dumps({{'ok':True}}))
""")
    fake.chmod(0o755)
    bootstrap = f"""import sys
sys.path.insert(0, {str(ENTRY.parent)!r})
import meshy_conversion, meshy
meshy_conversion.RESOLOOP_PROCESS_TIMEOUT_SECONDS = 0.2
raise SystemExit(meshy.main())
"""
    env = dict(os.environ, PATH=str(bindir) + os.pathsep + os.environ["PATH"])
    proc = run(
        [
            sys.executable,
            "-c",
            bootstrap,
            "--project",
            str(world[0]),
            "convert",
            "--operation",
            "asset",
            "--height",
            "2",
            "--parent",
            PARENT,
        ],
        env=env,
    )
    assert proc.returncode != 0
    assert "resoloop_export_failed" in proc.stderr
    time.sleep(0.6)
    assert not marker.exists(), "timed-out exporter descendant survived"
    assert_unpublished(world)


# Catch choosing the first GLB or trusting filenames instead of model.glb.
# The backup has valid manifest metadata but is NOT a GLB, so importing it fails.
def test_real_conversion_selects_official_primary_over_backup(world):
    manifest = json.loads(world[3].read_text())
    backup = world[1] / "source/model.glb"
    backup.write_bytes(b"not a GLB backup")
    manifest["downloads"]["files"].insert(
        0,
        {
            "key": "model.pre_remeshed_glb",
            "path": str(backup),
            "status": "written",
            "bytes": backup.stat().st_size,
            "sha256": hashlib.sha256(backup.read_bytes()).hexdigest(),
        },
    )
    world[3].write_text(json.dumps(manifest))
    original = world[2].read_bytes()
    proc = convert(world, "--allow-culling-change")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    conversion = json.loads(proc.stdout)
    assert conversion["source_sha256"] == hashlib.sha256(original).hexdigest()
    report = json.loads(Path(conversion["report"]).read_text())
    assert report["mesh_count"] == 2
    assert world[2].read_bytes() == original
    assert backup.read_bytes() == b"not a GLB backup"


# Catch rejecting schema1 manifests written before asset keys were preserved.
def test_real_conversion_accepts_single_legacy_unkeyed_glb(world):
    manifest = json.loads(world[3].read_text())
    manifest["downloads"]["files"][0].pop("key")
    world[3].write_text(json.dumps(manifest))
    proc = convert(world, "--allow-culling-change")
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert Path(json.loads(proc.stdout)["blend"]).is_file()


# Catch fallback to keyed backups or unkeyed candidates when the primary is
# absent/failed, and retain legacy ambiguity rejection.
@pytest.mark.parametrize(
    "keys,statuses",
    [
        (["model.pre_remeshed_glb"], ["written"]),
        ([None, "model.pre_remeshed_glb"], ["written", "written"]),
        (["model.glb", "model.pre_remeshed_glb"], ["failed", "written"]),
        ([None, None], ["written", "written"]),
    ],
    ids=[
        "missing-primary",
        "mixed-missing-primary",
        "failed-primary",
        "legacy-ambiguous",
    ],
)
def test_conversion_rejects_missing_primary_or_legacy_ambiguity(world, keys, statuses):
    manifest = json.loads(world[3].read_text())
    original = manifest["downloads"]["files"][0]
    files = []
    for key, status in zip(keys, statuses, strict=True):
        entry = dict(original, status=status)
        if key is None:
            entry.pop("key")
        else:
            entry["key"] = key
        files.append(entry)
    manifest["downloads"]["files"] = files
    world[3].write_text(json.dumps(manifest))
    before = world[3].read_bytes()
    proc = convert(world, env={"PATH": "/nonexistent"})
    assert proc.returncode != 0
    assert "unique_glb_required" in proc.stderr, proc.stdout + proc.stderr
    assert world[3].read_bytes() == before
    assert_unpublished(world)


# Catch primary selection bypassing the existing input safety checks just
# because a second keyed GLB is present. No Blender process may be needed.
@pytest.mark.parametrize("bad", ["hash", "size", "missing", "symlink", "outside"])
def test_selected_primary_still_requires_safe_unchanged_file(world, tmp_path, bad):
    manifest = json.loads(world[3].read_text())
    primary = manifest["downloads"]["files"][0]
    backup = world[1] / "source/backup.glb"
    shutil.copyfile(world[2], backup)
    manifest["downloads"]["files"].insert(
        0,
        {
            "key": "model.pre_remeshed_glb",
            "path": str(backup),
            "status": "written",
            "bytes": backup.stat().st_size,
            "sha256": hashlib.sha256(backup.read_bytes()).hexdigest(),
        },
    )
    if bad == "hash":
        primary["sha256"] = "0" * 64
    elif bad == "size":
        primary["bytes"] += 1
    elif bad == "missing":
        world[2].unlink()
    elif bad == "symlink":
        world[2].unlink()
        world[2].symlink_to(backup)
    else:
        outside = world[0] / "outside.glb"
        shutil.copyfile(world[2], outside)
        primary["path"] = str(outside)
    world[3].write_text(json.dumps(manifest))
    before = world[3].read_bytes()
    proc = convert(world, env={"PATH": "/nonexistent"})
    assert proc.returncode != 0
    expected = {
        "hash": "source_glb_changed",
        "size": "source_glb_changed",
        "missing": "source_glb_missing",
        "symlink": "symlink_forbidden",
        "outside": "source_outside_operation",
    }[bad]
    assert expected in proc.stderr, proc.stdout + proc.stderr
    assert world[3].read_bytes() == before
    assert_unpublished(world)


# Catch losing the post-processing hash/bytes/symlink check for the selected
# primary. The fake delegates actual Blender/export work, mutating only after
# export succeeds; none of that output may be published.
@pytest.mark.parametrize("change", ["hash", "size", "symlink"])
def test_selected_primary_is_rechecked_after_processing(world, tools, tmp_path, change):
    manifest = json.loads(world[3].read_text())
    backup = world[1] / "source/backup.glb"
    shutil.copyfile(world[2], backup)
    manifest["downloads"]["files"].insert(
        0,
        {
            "key": "model.pre_remeshed_glb",
            "path": str(backup),
            "status": "written",
            "bytes": backup.stat().st_size,
            "sha256": hashlib.sha256(backup.read_bytes()).hexdigest(),
        },
    )
    world[3].write_text(json.dumps(manifest))
    before = world[3].read_bytes()
    bindir = tmp_path / "bin"
    bindir.mkdir()
    fake = bindir / "resoloop"
    fake.write_text(f"""#!{sys.executable}
import pathlib, subprocess, sys
proc = subprocess.run([{tools!r}, *sys.argv[1:]], capture_output=True, text=True)
if proc.returncode == 0 and sys.argv[1:3] == ['blender', 'export']:
    primary = pathlib.Path({str(world[2])!r})
    if {change!r} == 'symlink':
        primary.unlink()
        primary.symlink_to({str(backup)!r})
    else:
        content = primary.read_bytes()
        primary.write_bytes(content + b'changed' if {change!r} == 'size'
                            else bytes([content[0] ^ 1]) + content[1:])
print(proc.stdout, end='')
print(proc.stderr, end='', file=sys.stderr)
sys.exit(proc.returncode)
""")
    fake.chmod(0o755)
    proc = convert(
        world,
        "--allow-culling-change",
        env={"PATH": str(bindir) + os.pathsep + os.environ["PATH"]},
    )
    assert proc.returncode != 0
    assert (
        "symlink_forbidden" if change == "symlink" else "source_glb_changed"
    ) in proc.stderr
    assert world[3].read_bytes() == before
    assert_unpublished(world)
