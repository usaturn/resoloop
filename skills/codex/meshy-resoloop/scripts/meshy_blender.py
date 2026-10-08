"""Offline static GLB adaptation, executed only by `resoloop blender run`.

No joining, baking, decimation, animation freezing or implicit material fallback.
Unsupported features produce a named rejection report instead of a usable blend.
"""

import argparse
import json
import math
import struct
import sys
from array import array
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


class Rejected(Exception):
    def __init__(self, name, reason):
        self.name, self.reason = name, reason
        super().__init__(f"{name}: {reason}")


def write_report(path, report):
    path.write_text(
        json.dumps(report, ensure_ascii=False, allow_nan=False, indent=2) + "\n"
    )


def preflight(path, *, allow_culling_change=False):
    """Reject lossy/static and external-file imports before Blender opens them."""
    with path.open("rb") as handle:
        header = handle.read(20)
        magic, version, length, json_length, chunk = struct.unpack("<4sIIII", header)
        if (
            magic != b"glTF"
            or version != 2
            or chunk != 0x4E4F534A
            or length != path.stat().st_size
        ):
            raise Rejected(path.name, "invalid_glb")
        document = json.loads(handle.read(json_length))
    for resource in document.get("buffers", []) + document.get("images", []):
        if "uri" in resource and not resource["uri"].startswith("data:"):
            raise Rejected(resource.get("name", path.name), "external_glb_resource")
    nodes = document.get("nodes", [])
    for animation in document.get("animations", []):
        target = animation.get("channels", [{}])[0].get("target", {}).get("node")
        name = (
            nodes[target].get("name", str(target))
            if target is not None
            else animation.get("name", path.name)
        )
        raise Rejected(name, "animation_unsupported")
    for node in nodes:
        if "skin" in node:
            raise Rejected(node.get("name", path.name), "rig_unsupported")
    for material in document.get("materials", []):
        name = material.get("name", path.name)
        if material.get("alphaMode", "OPAQUE") != "OPAQUE":
            raise Rejected(name, "non_opaque_material")
        if "occlusionTexture" in material:
            raise Rejected(name, "occlusion_requires_explicit_adaptation")
    material_culling = [
        {
            "name": material.get("name", path.name),
            "doubleSided": material.get("doubleSided", False),
        }
        for material in document.get("materials", [])
    ]
    # A primitive without a material still has glTF's default one-sided
    # material; absence of a materials array must not bypass consent.
    if any(
        "material" not in primitive
        for mesh in document.get("meshes", [])
        for primitive in mesh.get("primitives", [])
    ):
        material_culling.append({"name": "Default glTF material", "doubleSided": False})
    if material_culling and not allow_culling_change:
        raise Rejected(material_culling[0]["name"], "culling_preservation_unsupported")
    return material_culling


def socket_value(socket):
    value = socket.default_value
    return tuple(value) if hasattr(value, "__len__") else value


def image_input(material, link, space):
    node = link.from_node
    if (
        node.type != "TEX_IMAGE"
        or not node.image
        or link.from_socket.name not in {"Color", "Alpha"}
    ):
        raise Rejected(material.name, "direct_image_required")
    image = node.image
    if (
        image.source not in {"FILE", "GENERATED"}
        or image.type != "IMAGE"
        or node.projection != "FLAT"
        or node.extension != "REPEAT"
        or node.interpolation != "Linear"
        or node.inputs["Vector"].is_linked
    ):
        raise Rejected(material.name, "active_uv_flat_repeat_linear_required")
    if image.colorspace_settings.name != space:
        raise Rejected(material.name, "image_color_space_mismatch")
    width, height = image.size
    if width <= 0 or height <= 0 or not image.has_data:
        raise Rejected(image.name, "missing_image_pixels")
    return node


def split_channel(material, socket, index, cache):
    if not socket.is_linked:
        return
    link = socket.links[0]
    separate = link.from_node
    if separate.type not in {"SEPARATE_COLOR", "SEPRGB"}:
        node = image_input(material, link, "Non-Color")
        if link.from_socket.name == "Color":
            pixels = list(node.image.pixels)
            if any(
                max(pixels[i : i + 3]) - min(pixels[i : i + 3]) > 1e-5
                for i in range(0, len(pixels), 4)
            ):
                raise Rejected(material.name, "scalar_image_must_be_grayscale")
        return
    expected = {1: {"Green", "G"}, 2: {"Blue", "B"}}[index]
    if link.from_socket.name not in expected or (
        separate.type == "SEPARATE_COLOR" and separate.mode != "RGB"
    ):
        raise Rejected(material.name, "gltf_mr_channel_mismatch")
    incoming = separate.inputs[0]
    if not incoming.is_linked or incoming.links[0].from_socket.name != "Color":
        raise Rejected(material.name, "mr_image_required")
    source = image_input(material, incoming.links[0], "Non-Color").image
    identity = (source.name, index)
    image = cache.get(identity)
    if image is None:
        image = bpy.data.images.new(
            source.name + "_" + socket.name,
            width=source.size[0],
            height=source.size[1],
            alpha=True,
        )
        image.colorspace_settings.name = "Non-Color"
        pixels = array("f", [0]) * len(source.pixels)
        source.pixels.foreach_get(pixels)
        for i in range(0, len(pixels), 4):
            value = pixels[i + index]
            pixels[i : i + 4] = array("f", (value, value, value, 1))
        image.pixels.foreach_set(pixels)
        # Retain generated pixels before assigning the image to a new node.
        image.pack()
        cache[identity] = image
    node = material.node_tree.nodes.new("ShaderNodeTexImage")
    node.label = socket.name + " (glTF channel)"
    node.image = image
    material.node_tree.links.new(node.outputs["Color"], socket)


def adapt_material(material, defaults, cache):
    if (
        bpy.app.version < (5, 0, 0) and not material.use_nodes
    ) or not material.node_tree:
        raise Rejected(material.name, "principled_required")
    outputs = [
        n
        for n in material.node_tree.nodes
        if n.type == "OUTPUT_MATERIAL" and n.is_active_output
    ]
    if len(outputs) != 1 or not outputs[0].inputs["Surface"].is_linked:
        raise Rejected(material.name, "single_surface_required")
    output = outputs[0]
    if any(output.inputs[name].is_linked for name in ("Volume", "Displacement")):
        raise Rejected(material.name, "volume_displacement_unsupported")
    shader = output.inputs["Surface"].links[0].from_node
    if shader.type != "BSDF_PRINCIPLED":
        raise Rejected(material.name, "direct_principled_required")
    allowed = {"Base Color", "Metallic", "Roughness", "Normal", "Emission Color"}
    mapped = allowed | {"Emission Strength", "Alpha"}
    for socket in shader.inputs:
        if socket.is_linked and socket.name not in allowed:
            raise Rejected(material.name, "unsupported_link_" + socket.name)
        if (
            socket.name not in mapped
            and socket.name in defaults
            and socket_value(socket) != defaults[socket.name]
        ):
            raise Rejected(material.name, "unsupported_input_" + socket.name)
    if shader.inputs["Alpha"].default_value != 1:
        raise Rejected(material.name, "non_opaque_material")
    for name in ("Base Color", "Emission Color"):
        socket = shader.inputs[name]
        if socket.is_linked:
            if socket.links[0].from_socket.name != "Color":
                raise Rejected(material.name, "color_output_required")
            image_input(material, socket.links[0], "sRGB")
    normal = shader.inputs["Normal"]
    if normal.is_linked:
        node = normal.links[0].from_node
        if (
            node.type != "NORMAL_MAP"
            or node.space != "TANGENT"
            or node.uv_map
            or node.inputs["Strength"].is_linked
            or not node.inputs["Color"].is_linked
            or node.inputs["Color"].links[0].from_socket.name != "Color"
        ):
            raise Rejected(material.name, "tangent_normal_active_uv_required")
        image_input(material, node.inputs["Color"].links[0], "Non-Color")
    split_channel(material, shader.inputs["Metallic"], 2, cache)
    split_channel(material, shader.inputs["Roughness"], 1, cache)


def bounds(meshes):
    points = [
        obj.matrix_world @ vertex.co for obj in meshes for vertex in obj.data.vertices
    ]
    minimum = [min(p[i] for p in points) for i in range(3)]
    maximum = [max(p[i] for p in points) for i in range(3)]
    return {"min": minimum, "max": maximum, "height": maximum[2] - minimum[2]}


def convert(args):
    material_culling = preflight(
        args.input, allow_culling_change=args.allow_culling_change
    )
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(args.input))
    objects = list(bpy.context.scene.objects)
    meshes = [obj for obj in objects if obj.type == "MESH"]
    if not meshes:
        raise Rejected(args.input.name, "no_meshes")
    for obj in objects:
        if (
            obj.type not in {"MESH", "EMPTY"}
            or obj.animation_data
            or obj.constraints
            or obj.instance_type != "NONE"
        ):
            raise Rejected(obj.name, "static_mesh_hierarchy_required")
        if obj.type == "MESH":
            if obj.data.shape_keys or obj.modifiers:
                raise Rejected(obj.name, "rig_shape_keys_modifiers_unsupported")
            if not obj.data.uv_layers.active:
                raise Rejected(obj.name, "active_uv_required")
            obj.data.uv_layers.active.active_render = True
    temporary = bpy.data.materials.new("MeshyDefaults")
    # Blender 5 creates node materials by default; use_nodes is deprecated.
    if bpy.app.version < (5, 0, 0):
        temporary.use_nodes = True
    defaults = {
        s.name: socket_value(s)
        for s in temporary.node_tree.nodes.get("Principled BSDF").inputs
        if hasattr(s, "default_value")
    }
    bpy.data.materials.remove(temporary)
    # glTF 2.0 Default Material uses the schema defaults, not Blender's or
    # ResoLoop's unassigned-material fallback. Fill slots without changing face
    # indices so mixed authored/default primitives keep their original binding.
    # https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#default-material
    default_material = None
    for obj in meshes:
        slots = obj.data.materials
        if slots and all(slots):
            continue
        if default_material is None:
            default_material = bpy.data.materials.new("Default glTF material")
            if bpy.app.version < (5, 0, 0):
                default_material.use_nodes = True
            shader = default_material.node_tree.nodes.get("Principled BSDF")
            shader.inputs["Base Color"].default_value = (1, 1, 1, 1)
            shader.inputs["Metallic"].default_value = 1
            shader.inputs["Roughness"].default_value = 1
            shader.inputs["Alpha"].default_value = 1
            shader.inputs["Emission Color"].default_value = (0, 0, 0, 1)
            default_material.use_backface_culling = True
        if not slots:
            slots.append(default_material)
        else:
            for index, material in enumerate(slots):
                if material is None:
                    slots[index] = default_material
    materials = {m for obj in meshes for m in obj.data.materials if m}
    cache = {}
    for material in sorted(materials, key=lambda m: m.name):
        adapt_material(material, defaults, cache)
    # Rotate/scale roots only. Descendant meshes and pivots remain separate/editable.
    rotation = Matrix.Rotation(math.radians(args.yaw), 4, "Z")
    roots = [obj for obj in objects if not obj.parent]
    for obj in roots:
        obj.matrix_world = rotation @ obj.matrix_world
    bpy.context.view_layer.update()
    box = bounds(meshes)
    if not math.isfinite(box["height"]) or box["height"] <= 1e-8:
        raise Rejected(args.input.name, "positive_model_height_required")
    scale = args.height / box["height"]
    center = [(box["min"][i] + box["max"][i]) / 2 for i in range(3)]
    pivot = Vector(
        (center[0], center[1], box["min"][2] if args.origin == "ground" else center[2])
    )
    transform = Matrix.Scale(scale, 4) @ Matrix.Translation(-pivot)
    for obj in roots:
        obj.matrix_world = transform @ obj.matrix_world
    bpy.context.view_layer.update()
    bpy.context.scene.unit_settings.scale_length = 1
    images = [image for image in bpy.data.images if image.type == "IMAGE"]
    image_dir = args.output.parent / "images"
    image_dir.mkdir()
    for index, image in enumerate(images):
        # Save/reload authored pixels before pack; imported packed buffers can be
        # invalidated by repacking directly in Blender 5. Keep unused original MR.
        image.use_fake_user = True
        filename = f"image-{index}.png"
        saved = image_dir / filename
        image.file_format = "PNG"
        image.save(filepath=str(saved), save_copy=True)
        if image.packed_file:
            image.unpack(method="REMOVE")
        image.source = "FILE"
        image.filepath = str(saved)
        image.reload()
        image.pack()
        image.filepath = "//images/" + filename
    triangles = 0
    for obj in meshes:
        obj.data.calc_loop_triangles()
        triangles += len(obj.data.loop_triangles)
    bpy.ops.wm.save_as_mainfile(filepath=str(args.output))
    write_report(
        args.report,
        {
            "state": "completed",
            "blender_version": bpy.app.version_string,
            "blend": str(args.output),
            "input": str(args.input),
            "name": args.name,
            "height": args.height,
            "yaw": args.yaw,
            "origin": args.origin,
            "bounds": bounds(meshes),
            "mesh_count": len(meshes),
            "triangles": triangles,
            "materials": len(materials),
            "allow_culling_change": args.allow_culling_change,
            "material_culling": material_culling,
            "warnings": [
                {
                    **material,
                    "reason": "culling_preservation_unsupported",
                    "message": (
                        "ResoLoop export does not emit Culling; original glTF "
                        "doubleSided cannot be preserved. Only this discrepancy "
                        "was explicitly accepted. Runtime culling is unverified "
                        "without Reflection."
                    ),
                }
                for material in material_culling
            ],
            "objects": [
                {"name": obj.name, "parent": obj.parent.name if obj.parent else None}
                for obj in objects
            ],
            "images": [
                {
                    "name": image.name,
                    "width": image.size[0],
                    "height": image.size[1],
                    "color_space": image.colorspace_settings.name,
                }
                for image in images
            ],
        },
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--height", type=float, required=True)
    parser.add_argument("--yaw", type=float, required=True)
    parser.add_argument("--origin", choices=("ground", "center"), required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--allow-culling-change", action="store_true")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
    try:
        convert(args)
    except Rejected as error:
        write_report(
            args.report,
            {"state": "rejected", "name": error.name, "reason": error.reason},
        )
        raise
