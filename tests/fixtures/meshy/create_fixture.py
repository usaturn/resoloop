"""Generate tiny real GLBs with Blender; never download assets or call Meshy."""

import argparse
import math
import sys
from pathlib import Path

import bpy


def create(output, case):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    material = bpy.data.materials.new("FixtureMaterial")
    if bpy.app.version < (5, 0, 0):
        material.use_nodes = True
    material.use_backface_culling = case == "one_sided"
    shader = material.node_tree.nodes.get("Principled BSDF")
    albedo = bpy.data.images.new("AsymmetricAlbedo", width=2, height=2, alpha=True)
    albedo.colorspace_settings.name = "sRGB"
    albedo.pixels[:] = [1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 0, 1]
    albedo.pack()
    node = material.node_tree.nodes.new("ShaderNodeTexImage")
    node.image = albedo
    material.node_tree.links.new(node.outputs["Color"], shader.inputs["Base Color"])
    mr = bpy.data.images.new("PackedMR", width=2, height=2, alpha=True)
    mr.colorspace_settings.name = "Non-Color"
    mr.pixels[:] = [0.1, 0.25, 0.75, 1] * 4
    mr.pack()
    mr_node = material.node_tree.nodes.new("ShaderNodeTexImage")
    mr_node.image = mr
    separate = material.node_tree.nodes.new("ShaderNodeSeparateColor")
    material.node_tree.links.new(mr_node.outputs["Color"], separate.inputs["Color"])
    material.node_tree.links.new(separate.outputs["Blue"], shader.inputs["Metallic"])
    material.node_tree.links.new(separate.outputs["Green"], shader.inputs["Roughness"])
    if case == "transparency":
        shader.inputs["Alpha"].default_value = 0.5
        material.surface_render_method = "DITHERED"
    if case == "transmission":
        shader.inputs["Transmission Weight"].default_value = 0.6
    second_material = None
    if case == "mixed_culling":
        second_material = material.copy()
        second_material.name = "OneSidedMaterial"
        second_material.use_backface_culling = True
    parent = bpy.data.objects.new("FixtureHierarchy", None)
    bpy.context.collection.objects.link(parent)
    parent.location = (2, -1, 0.5)
    parts = (("LowerPart", (1, 0, 2)), ("UpperPart", (3, 0, 4)))
    if case == "tilted_tetrahedron":
        parts = (("TiltedTetrahedron", (1, 0, 2)),)
    for name, location in parts:
        if case == "tilted_tetrahedron":
            mesh = bpy.data.meshes.new(name)
            mesh.from_pydata(
                [(0, 0, 0), (2, 0, 0), (0, 1, 0), (0, 0, 3)],
                [],
                [(0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3)],
            )
            uv = mesh.uv_layers.new(name="UVMap")
            for polygon in mesh.polygons:
                for index, coords in zip(
                    polygon.loop_indices, [(0, 0), (1, 0), (0, 1)], strict=True
                ):
                    uv.data[index].uv = coords
            obj = bpy.data.objects.new(name, mesh)
            bpy.context.collection.objects.link(obj)
            obj.rotation_euler = tuple(math.radians(a) for a in (25, 35, 15))
        else:
            bpy.ops.mesh.primitive_cube_add(size=2)
            obj = bpy.context.object
        obj.name = name
        obj.parent = parent
        obj.location = location
        if case == "mixed_default_material":
            # Exercise empty slots before and after an authored material on
            # the same mesh; glTF export omits material on those primitives.
            for slot in (material, None) if name == "LowerPart" else (None, material):
                obj.data.materials.append(slot)
            for polygon in obj.data.polygons:
                polygon.material_index = polygon.index % 2
        elif case != "implicit_material":
            obj.data.materials.append(
                second_material if name == "UpperPart" and second_material else material
            )
        if case == "animation":
            obj.keyframe_insert(data_path="location", frame=1)
            obj.location.x += 1
            obj.keyframe_insert(data_path="location", frame=10)
    bpy.ops.export_scene.gltf(filepath=str(output), export_format="GLB")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--case", default="opaque")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
    create(args.output, args.case)
