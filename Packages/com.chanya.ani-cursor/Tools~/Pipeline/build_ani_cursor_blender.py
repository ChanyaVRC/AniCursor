"""Build one pixel-exact animated cursor display with Blender + Logo Tracer.

Run with Blender, not regular Python:
  blender --background --factory-startup --disable-autoexec --python-exit-code 1 \
    --python build_ani_cursor_blender.py -- MANIFEST LOGOTRACER_ZIP OUTPUT_FBX [REPORT]

Logo Tracer creates the global union object. Its result is then snapped back to
the source occupancy grid as alpha-signature shells: pixels that are visible in
exactly the same atlas frames share a closed plate. All shells live in one mesh
and touch on exact grid coordinates, so the animated outline changes without
union-frame ghosts, voxel gaps, or one object per pixel.
"""

from __future__ import annotations

import bmesh
import bpy
import json
import sys
import types
import zipfile
from pathlib import Path


def _arguments() -> list[str]:
    if "--" not in sys.argv:
        raise RuntimeError("Expected arguments after --")
    values = sys.argv[sys.argv.index("--") + 1 :]
    if len(values) < 3:
        raise RuntimeError("Expected MANIFEST LOGOTRACER_ZIP OUTPUT_FBX [REPORT]")
    return values


MANIFEST_PATH, LOGOTRACER_ZIP, OUTPUT_FBX, *REST = _arguments()
REPORT_PATH = REST[0] if REST else ""
manifest_path = Path(MANIFEST_PATH).resolve()
manifest_root = manifest_path.parent
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
if manifest.get("schema_version") != 3:
    raise RuntimeError("Only ANI Cursor manifest schema_version 3 is supported")


def _resolve(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else (manifest_root / path).resolve()


atlas_settings = manifest.get("atlas")
if not isinstance(atlas_settings, dict):
    raise RuntimeError("Manifest atlas must be an object")
atlas_frames = atlas_settings.get("frames")
if not isinstance(atlas_frames, list) or not atlas_frames:
    raise RuntimeError("Manifest atlas.frames must be a non-empty array")
atlas_frames_by_id = {}
for atlas_frame in atlas_frames:
    if not isinstance(atlas_frame, dict):
        raise RuntimeError("Manifest atlas frame must be an object")
    atlas_frame_id = atlas_frame.get("frame_id")
    if not atlas_frame_id:
        raise RuntimeError("Manifest atlas frame has no frame_id")
    atlas_frames_by_id[str(atlas_frame_id)] = atlas_frame

image_settings = manifest.get("image")
if not isinstance(image_settings, dict):
    raise RuntimeError("Manifest image must be an object")
SIZE = int(image_settings["width"])
if int(image_settings["height"]) != SIZE:
    raise RuntimeError("Manifest image must be square")

geometry_settings = manifest.get("geometry")
if not isinstance(geometry_settings, dict):
    raise RuntimeError("Manifest geometry must be an object")
GEOMETRY_MODE = str(geometry_settings["mode"])
if GEOMETRY_MODE != "global_alpha_signature_closed_plate":
    raise RuntimeError(f"Unsupported geometry mode: {GEOMETRY_MODE}")
DISPLAY_OBJECT_NAME = "CursorDisplay"
ALPHA_THRESHOLD = int(geometry_settings["alpha_threshold"])
if not 1 <= ALPHA_THRESHOLD <= 255:
    raise RuntimeError("Geometry alpha threshold must be between 1 and 255")
WORLD_SIZE = float(geometry_settings["world_size_m"])
THICKNESS = float(geometry_settings["thickness_m"])
if WORLD_SIZE <= 0.0 or THICKNESS <= 0.0:
    raise RuntimeError("Geometry world size and thickness must be positive")
HALF_THICKNESS = THICKNESS * 0.5
PIXEL_SIZE = WORLD_SIZE / SIZE


def load_logo_tracer(path: str):
    with zipfile.ZipFile(path) as archive:
        candidates = [
            name
            for name in archive.namelist()
            if name.endswith("/__init__.py") and "LogoTracer" in name
        ]
        if not candidates:
            raise RuntimeError(f"Logo Tracer __init__.py was not found in {path}")
        entry = min(candidates, key=lambda name: (name.count("/"), len(name)))
        source = archive.read(entry).decode("utf-8")

    # LogoTracer 1.21 predates Blender 5.x.  FLOAT/EXACT replaced FAST.
    source = source.replace('modbool.solver = "FAST"', 'modbool.solver = "EXACT"')
    # The input is padded from 32 to 36 pixels. Low quality adds 4x SIMPLE
    # subdivision, so nine base segments become an exact 36-segment trace grid.
    source = source.replace("cuts=10,", "cuts=8,")
    module = types.ModuleType("ani_cursor_logo_tracer_runtime")
    module.__file__ = str(Path(path).resolve()) + "/" + entry
    sys.modules[module.__name__] = module
    exec(compile(source, module.__file__, "exec"), module.__dict__)
    module.register()
    return module


def load_mask(path: Path) -> tuple[list[list[bool]], bpy.types.Image]:
    image = bpy.data.images.load(str(path), check_existing=False)
    width, height = image.size
    if width != SIZE or height != SIZE:
        raise RuntimeError(f"{path}: expected {SIZE}x{SIZE}, got {width}x{height}")
    pixels = list(image.pixels[:])
    mask = [
        [
            pixels[(y * width + x) * 4 + 3] >= ALPHA_THRESHOLD / 255.0
            for x in range(width)
        ]
        for y in range(height)
    ]
    # A valid ANI may intentionally contain a fully transparent frame. It adds
    # an all-false column to the signature basis and therefore needs no geometry.
    return mask, image


def padded_trace_image(source: bpy.types.Image, name: str) -> bpy.types.Image:
    width, height = source.size
    padding = 2
    padded_width = width + padding * 2
    padded_height = height + padding * 2
    source_pixels = list(source.pixels[:])
    destination = [0.0] * (padded_width * padded_height * 4)
    for y in range(height):
        for x in range(width):
            source_index = (y * width + x) * 4
            destination_index = ((y + padding) * padded_width + x + padding) * 4
            destination[destination_index : destination_index + 4] = source_pixels[
                source_index : source_index + 4
            ]
    image = bpy.data.images.new(name, width=padded_width, height=padded_height, alpha=True)
    image.pixels.foreach_set(destination)
    image.update()
    return image


def connected_components(mask: list[list[bool]]) -> list[set[tuple[int, int]]]:
    remaining = {
        (x, y)
        for y, row in enumerate(mask)
        for x, enabled in enumerate(row)
        if enabled
    }
    components = []
    while remaining:
        seed = min(remaining, key=lambda point: (point[1], point[0]))
        remaining.remove(seed)
        stack = [seed]
        component = {seed}
        while stack:
            x, y = stack.pop()
            for neighbor in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    component.add(neighbor)
                    stack.append(neighbor)
        components.append(component)
    return components


def alpha_signature_masks(
    frame_masks: list[list[list[bool]]],
) -> tuple[list[list[list[bool]]], list[dict]]:
    """Partition the global union by per-pixel frame visibility signature."""
    if not frame_masks:
        raise RuntimeError("No frame masks were provided for signature partitioning")

    groups: dict[tuple[bool, ...], list[tuple[int, int]]] = {}
    for y in range(SIZE):
        for x in range(SIZE):
            signature = tuple(mask[y][x] for mask in frame_masks)
            if any(signature):
                groups.setdefault(signature, []).append((x, y))

    ordered_groups = sorted(
        groups.items(),
        key=lambda item: (-len(item[1]), tuple(int(value) for value in item[0])),
    )
    masks: list[list[list[bool]]] = []
    reports: list[dict] = []
    for signature, pixels in ordered_groups:
        pixel_set = set(pixels)
        mask = [
            [(x, y) in pixel_set for x in range(SIZE)]
            for y in range(SIZE)
        ]
        components = connected_components(mask)
        masks.append(mask)
        reports.append(
            {
                "pixels": len(pixels),
                "components": len(components),
                "visible_frame_count": sum(signature),
            }
        )

    if not masks:
        raise RuntimeError("The global alpha-signature union is empty")
    return masks, reports


def mask_image(mask: list[list[bool]], name: str) -> bpy.types.Image:
    """Create a Blender RGBA image whose alpha contains the supplied grid mask."""
    pixels: list[float] = []
    for row in mask:
        for enabled in row:
            alpha = 1.0 if enabled else 0.0
            pixels.extend((1.0, 1.0, 1.0, alpha))
    image = bpy.data.images.new(name, width=SIZE, height=SIZE, alpha=True)
    image.pixels.foreach_set(pixels)
    image.update()
    return image


def grid_position(x: int, y: int, z: float) -> tuple[float, float, float]:
    return ((x - SIZE / 2) * PIXEL_SIZE, (y - SIZE / 2) * PIXEL_SIZE, z)


def frame_uv(x: float, y: float) -> tuple[float, float]:
    """Map a grid coordinate to local 0..1 UV; Unity supplies absolute atlas ST."""
    return (x / SIZE, y / SIZE)


def build_exact_plate(
    name: str,
    shell_masks: list[list[list[bool]]],
    signature_basis_frame_count: int,
) -> bpy.types.Mesh:
    vertices: list[tuple[float, float, float]] = []
    faces: list[tuple[int, ...]] = []
    face_uvs: list[tuple[tuple[float, float], ...]] = []

    for shell_mask in shell_masks:
        # Reset component vertex maps for every shell. Different alpha signatures
        # may touch at the same XY edge, but must remain separate closed solids so
        # either side can be independently hidden by the atlas alpha.
        for component in connected_components(shell_mask):
            vertex_indices: dict[tuple[int, int, int, tuple[int, int]], int] = {}

            def local_corner_group(
                grid_x: int, grid_y: int, cell_x: int, cell_y: int
            ) -> tuple[int, int]:
                """Separate diagonal branches that only meet at this grid corner."""
                incident = {
                    point
                    for point in (
                        (grid_x - 1, grid_y - 1),
                        (grid_x, grid_y - 1),
                        (grid_x - 1, grid_y),
                        (grid_x, grid_y),
                    )
                    if point in component
                }
                seed = (cell_x, cell_y)
                reachable = {seed}
                stack = [seed]
                while stack:
                    current_x, current_y = stack.pop()
                    for neighbor in (
                        (current_x - 1, current_y),
                        (current_x + 1, current_y),
                        (current_x, current_y - 1),
                        (current_x, current_y + 1),
                    ):
                        if neighbor in incident and neighbor not in reachable:
                            reachable.add(neighbor)
                            stack.append(neighbor)
                return min(reachable)

            def vertex(x: int, y: int, z_side: int, cell_x: int, cell_y: int) -> int:
                key = (x, y, z_side, local_corner_group(x, y, cell_x, cell_y))
                if key not in vertex_indices:
                    vertex_indices[key] = len(vertices)
                    vertices.append(grid_position(x, y, HALF_THICKNESS * z_side))
                return vertex_indices[key]

            for x, y in sorted(component, key=lambda point: (point[1], point[0])):
                front = (
                    vertex(x, y, 1, x, y),
                    vertex(x + 1, y, 1, x, y),
                    vertex(x + 1, y + 1, 1, x, y),
                    vertex(x, y + 1, 1, x, y),
                )
                back = (
                    vertex(x, y, -1, x, y),
                    vertex(x, y + 1, -1, x, y),
                    vertex(x + 1, y + 1, -1, x, y),
                    vertex(x + 1, y, -1, x, y),
                )
                uv = (
                    frame_uv(x, y),
                    frame_uv(x + 1, y),
                    frame_uv(x + 1, y + 1),
                    frame_uv(x, y + 1),
                )
                faces.extend((front, back))
                face_uvs.extend((uv, (uv[0], uv[3], uv[2], uv[1])))

                def wall_uv(
                    start: tuple[int, int],
                    end: tuple[int, int],
                    inward: tuple[float, float],
                ) -> tuple[tuple[float, float], ...]:
                    # Shift half a pixel into this signature shell. This is
                    # essential: sampling the geometric boundary could pick the
                    # neighbouring signature's transparent texel and erase a wall.
                    # Collinear edges in one signature retain matching endpoints,
                    # so the optimizer may still dissolve each straight wall run.
                    uv_start = frame_uv(
                        start[0] + inward[0] * 0.5,
                        start[1] + inward[1] * 0.5,
                    )
                    uv_end = frame_uv(
                        end[0] + inward[0] * 0.5,
                        end[1] + inward[1] * 0.5,
                    )
                    return (uv_start, uv_end, uv_end, uv_start)

                if (x, y - 1) not in component:
                    faces.append(
                        (vertex(x, y, -1, x, y), vertex(x + 1, y, -1, x, y), vertex(x + 1, y, 1, x, y), vertex(x, y, 1, x, y))
                    )
                    face_uvs.append(wall_uv((x, y), (x + 1, y), (0.0, 1.0)))
                if (x + 1, y) not in component:
                    faces.append(
                        (vertex(x + 1, y, -1, x, y), vertex(x + 1, y + 1, -1, x, y), vertex(x + 1, y + 1, 1, x, y), vertex(x + 1, y, 1, x, y))
                    )
                    face_uvs.append(wall_uv((x + 1, y), (x + 1, y + 1), (-1.0, 0.0)))
                if (x, y + 1) not in component:
                    faces.append(
                        (vertex(x + 1, y + 1, -1, x, y), vertex(x, y + 1, -1, x, y), vertex(x, y + 1, 1, x, y), vertex(x + 1, y + 1, 1, x, y))
                    )
                    face_uvs.append(wall_uv((x + 1, y + 1), (x, y + 1), (0.0, -1.0)))
                if (x - 1, y) not in component:
                    faces.append(
                        (vertex(x, y + 1, -1, x, y), vertex(x, y, -1, x, y), vertex(x, y, 1, x, y), vertex(x, y + 1, 1, x, y))
                    )
                    face_uvs.append(wall_uv((x, y + 1), (x, y), (1.0, 0.0)))

    mesh = bpy.data.meshes.new(name + "_Mesh")
    mesh.from_pydata(vertices, [], faces)
    uv_layer = mesh.uv_layers.new(name="UVMap")
    for polygon, uvs in zip(mesh.polygons, face_uvs):
        polygon.use_smooth = False
        for loop_index, uv in zip(polygon.loop_indices, uvs):
            uv_layer.data[loop_index].uv = uv
    mesh.update(calc_edges=True)

    # Collapse the cell grid on caps and on straight wall runs.  UVs are affine
    # within every such surface, so the 32 px point-sampled shape is unchanged.
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.normal_update()
    source_non_manifold = [edge for edge in bm.edges if len(edge.link_faces) != 2]
    source_zero_area = [face for face in bm.faces if face.calc_area() <= 1e-14]
    if source_non_manifold or source_zero_area:
        bm.free()
        raise RuntimeError(
            f"{name}: invalid pixel-cell plate before optimization: "
            f"non-manifold={len(source_non_manifold)}, zero-area={len(source_zero_area)}"
        )
    bm.normal_update()
    coplanar_edges = [
        edge
        for edge in bm.edges
        if len(edge.link_faces) == 2
        and edge.link_faces[0].normal.dot(edge.link_faces[1].normal) > 0.999999
    ]
    # face-split keeps a legal bridge at one-corner pixel contacts instead of
    # turning the cap into a pinched n-gon (TextSelect contains such contacts).
    bmesh.ops.dissolve_edges(bm, edges=coplanar_edges, use_verts=True, use_face_split=True)
    bm.normal_update()
    bmesh.ops.triangulate(
        bm,
        faces=[face for face in bm.faces if abs(face.normal.z) > 0.999999],
        quad_method="FIXED",
        ngon_method="BEAUTY",
    )
    bm.normal_update()
    non_manifold = [edge for edge in bm.edges if len(edge.link_faces) != 2]
    zero_area = [face for face in bm.faces if face.calc_area() <= 1e-14]
    optimization = "dissolved_caps"
    if non_manifold or zero_area:
        # A region that meets itself at one grid corner cannot be represented as
        # one legal n-gon. Keep that cap's exact pixel quads instead; it is still
        # a single closed plate and considerably smaller than per-frame meshes.
        bm.free()
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bm.normal_update()
        bmesh.ops.triangulate(
            bm,
            faces=[face for face in bm.faces if abs(face.normal.z) > 0.999999],
            quad_method="FIXED",
            ngon_method="BEAUTY",
        )
        bm.normal_update()
        non_manifold = [edge for edge in bm.edges if len(edge.link_faces) != 2]
        zero_area = [face for face in bm.faces if face.calc_area() <= 1e-14]
        if non_manifold or zero_area:
            bm.free()
            raise RuntimeError(
                f"{name}: fallback plate is invalid: "
                f"non-manifold={len(non_manifold)}, zero-area={len(zero_area)}"
            )
        optimization = "pixel_cap_fallback"
    bm.to_mesh(mesh)
    bm.free()
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    mesh.validate(clean_customdata=False)
    mesh.update(calc_edges=True)
    mesh["cap_optimization"] = optimization
    mesh["uv_mode"] = "normalized_frame"
    mesh["signature_shell_count"] = len(shell_masks)
    mesh["signature_basis_frame_count"] = signature_basis_frame_count
    return mesh


def trace_with_logo_tracer(image: bpy.types.Image, object_name: str):
    scene = bpy.context.scene
    scene.logo_tracer_img = image
    scene.logo_tracer_img_alpha = True
    if bpy.ops.preview.trace() != {"FINISHED"}:
        raise RuntimeError(f"{object_name}: Logo Tracer preview failed")
    props = scene.logo_tracer_props[0]
    props.quality = "Low"
    props.threshold = 0.5
    props.smooth = 0.0
    props.triangulate = False
    form = bpy.data.objects.get(props.form_obj)
    if form is None:
        raise RuntimeError(f"{object_name}: Logo Tracer form object was not created")
    displacement = form.modifiers.get("Logo_tracer_displace")
    if displacement and displacement.texture:
        texture = displacement.texture
        if hasattr(texture, "use_interpolation"):
            texture.use_interpolation = False
        if hasattr(texture, "filter_size"):
            texture.filter_size = 0.1
    if bpy.ops.apply.logotrace(output_mode=0) != {"FINISHED"}:
        raise RuntimeError(f"{object_name}: Logo Tracer apply failed")
    obj = bpy.context.view_layer.objects.active
    if obj is None or obj.type != "MESH":
        raise RuntimeError(f"{object_name}: Logo Tracer produced no mesh")
    trace_stats = {
        "vertices": len(obj.data.vertices),
        "faces": len(obj.data.polygons),
    }
    obj.name = object_name
    return obj, trace_stats


def validate_object(
    obj: bpy.types.Object,
    opaque_pixels: int,
    signature_shell_count: int,
    signature_component_count: int,
    signature_basis_frame_count: int,
) -> dict:
    mesh = obj.data
    mesh.calc_loop_triangles()
    bm = bmesh.new()
    bm.from_mesh(mesh)
    non_manifold = sum(1 for edge in bm.edges if len(edge.link_faces) != 2)
    zero_area = sum(1 for face in bm.faces if face.calc_area() <= 1e-14)
    bm.free()
    z_levels = sorted({round(vertex.co.z, 8) for vertex in mesh.vertices})
    uv_layer = mesh.uv_layers.active
    uv_values = [tuple(loop.uv) for loop in uv_layer.data] if uv_layer else []
    uv_bounds = (
        [min(uv[0] for uv in uv_values), min(uv[1] for uv in uv_values)],
        [max(uv[0] for uv in uv_values), max(uv[1] for uv in uv_values)],
    ) if uv_values else (None, None)
    return {
        "name": obj.name,
        "opaque_union_pixels": opaque_pixels,
        "vertices": len(mesh.vertices),
        "faces": len(mesh.polygons),
        "triangles": len(mesh.loop_triangles),
        "non_manifold_edges": non_manifold,
        "zero_area_faces": zero_area,
        "smooth_faces": sum(1 for polygon in mesh.polygons if polygon.use_smooth),
        "z_levels": z_levels,
        "thickness_m": round(max(z_levels) - min(z_levels), 8),
        "uv_layers": [layer.name for layer in mesh.uv_layers],
        "uv_bounds_min": uv_bounds[0],
        "uv_bounds_max": uv_bounds[1],
        "uv_mode": "normalized_frame",
        "signature_shells": signature_shell_count,
        "signature_components": signature_component_count,
        "signature_basis_frames": signature_basis_frame_count,
        "materials": [material.name for material in mesh.materials if material],
        "cap_optimization": mesh.get("cap_optimization", "unknown"),
    }


bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = "METRIC"
scene.unit_settings.length_unit = "METERS"
scene.unit_settings.scale_length = 1.0
logo_tracer = load_logo_tracer(LOGOTRACER_ZIP)
shared_material = bpy.data.materials.new("AniCursorAtlas")
reports = []

cursor_entries = manifest.get("cursors")
if not isinstance(cursor_entries, list) or not cursor_entries:
    raise RuntimeError("Manifest cursors must be a non-empty array")

basis_frame_ids = geometry_settings.get("signature_basis_frame_ids")
if not isinstance(basis_frame_ids, list) or not basis_frame_ids:
    raise RuntimeError("Geometry signature basis must contain at least one frame id")
basis_frame_ids = [str(frame_id) for frame_id in basis_frame_ids]
if len(set(basis_frame_ids)) != len(basis_frame_ids):
    raise RuntimeError("Geometry signature basis contains duplicate frame ids")

frame_masks: list[list[list[bool]]] = []
source_images: list[bpy.types.Image] = []
for frame_id in basis_frame_ids:
    atlas_frame = atlas_frames_by_id.get(frame_id)
    if atlas_frame is None:
        raise RuntimeError(f"Geometry signature basis references unknown frame {frame_id}")
    frame_file = atlas_frame.get("file")
    if not frame_file:
        raise RuntimeError(f"Atlas frame {frame_id} has no source file")
    frame_mask, frame_image = load_mask(_resolve(str(frame_file)))
    frame_masks.append(frame_mask)
    source_images.append(frame_image)

shell_masks, signature_reports = alpha_signature_masks(frame_masks)
global_union_mask = [
    [any(mask[y][x] for mask in frame_masks) for x in range(SIZE)]
    for y in range(SIZE)
]
opaque_union_pixels = sum(sum(row) for row in global_union_mask)
signature_component_count = sum(item["components"] for item in signature_reports)

# Logo Tracer still authors the single source object from the complete silhouette;
# the pixel-exact signature reconstruction replaces only its generated mesh data.
union_image = mask_image(global_union_mask, DISPLAY_OBJECT_NAME + "_GlobalUnion")
trace_image = padded_trace_image(union_image, DISPLAY_OBJECT_NAME + "_LogoTracer36")
obj, trace_stats = trace_with_logo_tracer(trace_image, DISPLAY_OBJECT_NAME)
old_mesh = obj.data
obj.data = build_exact_plate(DISPLAY_OBJECT_NAME, shell_masks, len(frame_masks))
obj.data.materials.append(shared_material)
obj.location = (0.0, 0.0, 0.0)
obj.rotation_euler = (0.0, 0.0, 0.0)
obj.scale = (1.0, 1.0, 1.0)
if old_mesh.users == 0:
    bpy.data.meshes.remove(old_mesh)
reports.append(
    {
        "logo_tracer": trace_stats,
        "signature_group_summary": signature_reports,
        **validate_object(
            obj,
            opaque_union_pixels,
            len(shell_masks),
            signature_component_count,
            len(frame_masks),
        ),
    }
)
objects = [obj]

for source_image in source_images:
    if source_image.users == 0:
        bpy.data.images.remove(source_image)

bpy.ops.object.select_all(action="DESELECT")
for obj in objects:
    obj.select_set(True)
bpy.context.view_layer.objects.active = objects[0]
Path(OUTPUT_FBX).resolve().parent.mkdir(parents=True, exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=str(Path(OUTPUT_FBX).resolve()),
    use_selection=True,
    object_types={"MESH"},
    global_scale=1.0,
    apply_unit_scale=True,
    apply_scale_options="FBX_SCALE_UNITS",
    use_space_transform=True,
    bake_space_transform=True,
    axis_forward="-Z",
    axis_up="Y",
    use_mesh_modifiers=True,
    add_leaf_bones=False,
    bake_anim=False,
    mesh_smooth_type="FACE",
    path_mode="AUTO",
    embed_textures=False,
)

payload = {
    "generator": "Blender Logo Tracer 1.21 + global alpha-signature closed-plate postprocess",
    "geometry_mode": GEOMETRY_MODE,
    "object_count": 1,
    "object_name": DISPLAY_OBJECT_NAME,
    "size_px": SIZE,
    "world_size_m": WORLD_SIZE,
    "thickness_m": THICKNESS,
    "pixel_gap_m": 0.0,
    "uv_mode": "normalized_frame",
    "alpha_threshold": ALPHA_THRESHOLD,
    "signature_basis_frame_count": len(frame_masks),
    "signature_shell_count": len(shell_masks),
    "signature_component_count": signature_component_count,
    "total_vertices": sum(item["vertices"] for item in reports),
    "total_triangles": sum(item["triangles"] for item in reports),
    "objects": reports,
}
if REPORT_PATH:
    report_file = Path(REPORT_PATH).resolve()
    report_file.parent.mkdir(parents=True, exist_ok=True)
    report_file.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
print("ANI_CURSOR_BUILD_REPORT=" + json.dumps(payload, ensure_ascii=False))
