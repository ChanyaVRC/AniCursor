"""Prepare Windows ANI cursors for the Blender/Unity cursor build pipeline.

Outputs are deliberately renderer-agnostic: exact 32 px RGBA frames, a
point-sampled alpha atlas, per-cursor menu icons, per-cursor union masks, and a
JSON manifest with literal and effective ANI playback timing.  Blender groups
pixels by their alpha signature across every atlas frame, so all cursor variants
can share one closed, gap-free 4 mm mesh without retaining union-frame ghosts.

The parser/converter is provided by the MIT-licensed ``ani-extract`` package.
Run this script with that project's virtual-environment Python, or pass
``--ani-extract-root`` / set ``ANI_EXTRACT_ROOT``.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import struct
import sys
import unicodedata
from collections import deque
from dataclasses import dataclass
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 3
JIFFIES_PER_SECOND = 60


def _bootstrap_ani_extract() -> None:
    """Make a source checkout importable before third-party imports run."""
    candidates: list[Path] = []
    for index, argument in enumerate(sys.argv):
        if argument == "--ani-extract-root" and index + 1 < len(sys.argv):
            candidates.append(Path(sys.argv[index + 1]))
        elif argument.startswith("--ani-extract-root="):
            candidates.append(Path(argument.split("=", 1)[1]))
    if os.environ.get("ANI_EXTRACT_ROOT"):
        candidates.append(Path(os.environ["ANI_EXTRACT_ROOT"]))
    for candidate in candidates:
        source_directory = candidate.expanduser() / "src"
        if source_directory.is_dir():
            sys.path.insert(0, str(source_directory.resolve()))


_bootstrap_ani_extract()

try:
    from PIL import Image
    from ani_extract import __version__ as ani_extract_version
    from ani_extract import open_entry, parse_ani, parse_icon_container
    from ani_extract.riff import walk
except ModuleNotFoundError as error:  # pragma: no cover - exercised by CLI setup failures
    raise SystemExit(
        "ani-extract/Pillow could not be imported. Run with "
        "<ani-extract>\\.venv\\Scripts\\python.exe, pass --ani-extract-root, "
        "or set ANI_EXTRACT_ROOT."
    ) from error


@dataclass(frozen=True)
class SelectedFrame:
    image: Image.Image
    hotspot: tuple[int, int] | None
    original_width: int
    original_height: int
    bit_count: int
    selection: str


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _canonical_path(path: Path) -> str:
    return unicodedata.normalize("NFC", path.as_posix())


def _safe_asset_name(value: str, fallback_hash: str) -> str:
    normalized = unicodedata.normalize("NFKC", value).strip()
    normalized = re.sub(r"[<>:\"/\\|?*\x00-\x1f]+", "_", normalized)
    normalized = re.sub(r"\s+", "_", normalized).strip("._ ")
    return normalized[:80] or f"Cursor_{fallback_hash[:12]}"


def _read_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Preset must contain a JSON object: {path}")
    return value


def _write_json(path: Path, value: object) -> None:
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def _find_sources(source_directory: Path, pattern: str) -> list[Path]:
    if not source_directory.is_dir():
        raise FileNotFoundError(f"Source directory not found: {source_directory}")
    sources = [path.resolve() for path in source_directory.rglob(pattern) if path.is_file()]
    sources = [path for path in sources if path.suffix.casefold() == ".ani"]
    sources.sort(key=lambda path: _canonical_path(path.relative_to(source_directory)).casefold())
    if not sources:
        raise ValueError(f"No ANI files matched {pattern!r} under {source_directory}")
    return sources


def _literal_playback_chunks(data: bytes) -> tuple[list[int] | None, list[int] | None]:
    """Read literal seq/rate DWORD arrays before ani-extract applies defaults."""
    if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"ACON":
        return None, None
    riff_end = min(8 + struct.unpack_from("<I", data, 4)[0], len(data))
    raw_sequence: list[int] | None = None
    raw_rates: list[int] | None = None
    for chunk in walk(data, 12, riff_end):
        if chunk.identifier not in (b"seq ", b"rate"):
            continue
        payload = chunk.payload(data)
        count = len(payload) // 4
        values = list(struct.unpack_from(f"<{count}I", payload, 0)) if count else []
        if chunk.identifier == b"seq ":
            raw_sequence = values
        else:
            raw_rates = values
    return raw_sequence, raw_rates


def _select_frame(payload: bytes, size: int) -> SelectedFrame:
    """Choose the best embedded image and make an exact nearest-neighbor square."""
    container = parse_icon_container(payload)
    exact = [entry for entry in container.entries if entry.width == size and entry.height == size]
    if exact:
        entry = max(exact, key=lambda item: item.bit_count)
        image = open_entry(entry).convert("RGBA")
        selection = "exact"
    else:
        entry = min(
            container.entries,
            key=lambda item: (
                abs(item.width - size) + abs(item.height - size),
                -(item.width * item.height),
                -item.bit_count,
            ),
        )
        image = open_entry(entry).convert("RGBA").resize(
            (size, size), Image.Resampling.NEAREST
        )
        selection = "nearest-neighbor-resize"

    hotspot = entry.hotspot
    if hotspot is not None and (entry.width, entry.height) != (size, size):
        hotspot = (
            round(hotspot[0] * size / entry.width),
            round(hotspot[1] * size / entry.height),
        )
    return SelectedFrame(
        image=image,
        hotspot=hotspot,
        original_width=entry.width,
        original_height=entry.height,
        bit_count=entry.bit_count,
        selection=selection,
    )


def _frame_id(image: Image.Image, size: int) -> str:
    payload = f"RGBA:{size}x{size}:".encode("ascii") + image.convert("RGBA").tobytes()
    return f"f-{_sha256(payload)}"


def _resolve_atlas_size(value: str | int, unique_count: int, cell_size: int) -> int:
    if isinstance(value, int) or str(value).isdigit():
        result = int(value)
        if result < cell_size or result % cell_size:
            raise ValueError("Atlas size must be a positive multiple of the frame size.")
        if (result // cell_size) ** 2 < unique_count:
            raise ValueError(f"Atlas {result}x{result} cannot fit {unique_count} frames.")
        return result

    mode = str(value).casefold()
    cells_per_side = max(1, math.ceil(math.sqrt(unique_count)))
    if mode == "minimal":
        return cells_per_side * cell_size
    if mode != "auto":
        raise ValueError("Atlas size must be an integer, 'auto', or 'minimal'.")
    result = cell_size
    while result < cells_per_side * cell_size:
        result *= 2
    return result


def _preset_binding(
    preset: dict[str, Any], source: Path, source_hash: str, fallback_value: int
) -> dict[str, Any]:
    mappings = preset.get("cursors", {})
    if not isinstance(mappings, dict):
        raise ValueError("preset.cursors must be an object keyed by ANI file name or stem")
    selected = mappings.get(source.name, mappings.get(source.stem, {}))
    if not isinstance(selected, dict):
        raise ValueError(f"Preset entry for {source.name} must be an object")

    asset_name = _safe_asset_name(
        str(selected.get("asset_name", source.stem)), source_hash
    )
    return {
        "asset_name": asset_name,
        "enabled_by_default": bool(selected.get("enabled_by_default", False)),
        "menu_label": str(selected.get("menu_label", source.stem)),
        "menu_order": int(selected.get("menu_order", fallback_value)),
        "parameter_value": int(selected.get("parameter_value", fallback_value)),
    }


def _make_union_mask(
    images: list[Image.Image], sequence: list[int], size: int, alpha_threshold: int
) -> tuple[Image.Image, int, int]:
    pixels = bytearray(size * size)
    for frame_index in sorted(set(sequence)):
        alpha = images[frame_index].getchannel("A").tobytes()
        for index, value in enumerate(alpha):
            if value >= alpha_threshold:
                pixels[index] = 255

    # A solid cursor plate may keep concave outside edges, but it must not have
    # enclosed transparent pinholes. Mark the transparent background reachable
    # from the image border with 4-connectivity, then fill only the unreachable
    # transparent pixels. This preserves the exact outer pixel silhouette.
    exterior = bytearray(size * size)
    pending: deque[int] = deque()

    def enqueue_if_background(x: int, y: int) -> None:
        index = y * size + x
        if pixels[index] == 0 and exterior[index] == 0:
            exterior[index] = 1
            pending.append(index)

    for coordinate in range(size):
        enqueue_if_background(coordinate, 0)
        enqueue_if_background(coordinate, size - 1)
        enqueue_if_background(0, coordinate)
        enqueue_if_background(size - 1, coordinate)

    while pending:
        index = pending.popleft()
        x = index % size
        y = index // size
        if x > 0:
            enqueue_if_background(x - 1, y)
        if x + 1 < size:
            enqueue_if_background(x + 1, y)
        if y > 0:
            enqueue_if_background(x, y - 1)
        if y + 1 < size:
            enqueue_if_background(x, y + 1)

    filled_hole_pixels = 0
    for index, value in enumerate(pixels):
        if value == 0 and exterior[index] == 0:
            pixels[index] = 255
            filled_hole_pixels += 1

    alpha = Image.frombytes("L", (size, size), bytes(pixels))
    # Store the occupancy in PNG alpha, because Logo Tracer traces image alpha.
    # A plain grayscale PNG is decoded as fully opaque by Blender.
    mask = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    mask.putalpha(alpha)
    return mask, sum(1 for value in pixels if value), filled_hole_pixels


def prepare(
    source_directory: Path,
    output_directory: Path,
    *,
    preset: dict[str, Any],
    pattern: str,
    size: int,
    atlas_size_setting: str | int,
    alpha_threshold: int,
) -> dict[str, Any]:
    """Prepare a self-contained deterministic staging bundle."""
    sources = _find_sources(source_directory, pattern)
    output_directory.mkdir(parents=True, exist_ok=True)
    if any(output_directory.iterdir()):
        raise ValueError(f"Output directory must be empty: {output_directory}")

    frames_directory = output_directory / "frames"
    icons_directory = output_directory / "icons"
    masks_directory = output_directory / "masks"
    frames_directory.mkdir()
    icons_directory.mkdir()
    masks_directory.mkdir()

    unique_images: dict[str, Image.Image] = {}
    cursors: list[dict[str, Any]] = []
    seen_asset_names: set[str] = set()
    total_stored_frames = 0
    total_steps = 0

    geometry_preset = preset.get("geometry", {})
    if not isinstance(geometry_preset, dict):
        raise ValueError("preset.geometry must be an object when present")
    world_size_m = float(geometry_preset.get("world_size_m", 0.12))
    thickness_m = float(geometry_preset.get("thickness_m", 0.004))
    if world_size_m <= 0.0 or thickness_m <= 0.0:
        raise ValueError("Geometry world size and thickness must be positive.")

    for cursor_index, source in enumerate(sources):
        source_bytes = source.read_bytes()
        source_hash = _sha256(source_bytes)
        ani = parse_ani(source_bytes)
        literal_sequence, literal_rates = _literal_playback_chunks(source_bytes)
        binding = _preset_binding(preset, source, source_hash, cursor_index)
        asset_name = binding["asset_name"]
        folded_asset_name = asset_name.casefold()
        if folded_asset_name in seen_asset_names:
            raise ValueError(f"Duplicate generated asset_name: {asset_name}")
        seen_asset_names.add(folded_asset_name)

        selected_frames: list[SelectedFrame] = []
        stored_frames: list[dict[str, Any]] = []
        frame_ids: list[str] = []
        warnings: list[str] = []
        for frame_index, payload in enumerate(ani.frames):
            selected = _select_frame(payload, size)
            selected_frames.append(selected)
            frame_id = _frame_id(selected.image, size)
            unique_images.setdefault(frame_id, selected.image)
            frame_ids.append(frame_id)
            if selected.selection != "exact":
                warnings.append(
                    f"frame {frame_index}: resized {selected.original_width}x"
                    f"{selected.original_height} to {size}x{size} using nearest-neighbor"
                )
            stored_frames.append(
                {
                    "bit_count": selected.bit_count,
                    "frame_id": frame_id,
                    "hotspot": list(selected.hotspot) if selected.hotspot else None,
                    "index": frame_index,
                    "original_size": [selected.original_width, selected.original_height],
                    "selection": selected.selection,
                }
            )

        steps: list[dict[str, Any]] = []
        start_jiffy = 0
        for step_index, (source_frame_index, duration_jiffies) in enumerate(
            zip(ani.sequence, ani.rates, strict=True)
        ):
            steps.append(
                {
                    "duration_jiffies": duration_jiffies,
                    "duration_ms": round(duration_jiffies * 1000 / JIFFIES_PER_SECOND),
                    "duration_seconds": duration_jiffies / JIFFIES_PER_SECOND,
                    "frame_id": frame_ids[source_frame_index],
                    "source_frame_index": source_frame_index,
                    "start_jiffy": start_jiffy,
                    "start_time_seconds": start_jiffy / JIFFIES_PER_SECOND,
                    "step_index": step_index,
                }
            )
            start_jiffy += duration_jiffies

        first_frame_index = ani.sequence[0]
        icon_path = icons_directory / f"{asset_name}.png"
        selected_frames[first_frame_index].image.save(icon_path, "PNG", optimize=False)
        union_mask, opaque_pixels, filled_hole_pixels = _make_union_mask(
            [frame.image for frame in selected_frames],
            ani.sequence,
            size,
            alpha_threshold,
        )
        union_mask_path = masks_directory / f"{asset_name}.png"
        union_mask.save(union_mask_path, "PNG", optimize=False)

        header = ani.header
        cursors.append(
            {
                "author": ani.author,
                "binding": binding,
                "cursor_id": f"cursor-{_sha256(_canonical_path(source.relative_to(source_directory)).encode('utf-8'))[:8]}-{source_hash[:8]}",
                "header": None
                if header is None
                else {
                    "bit_count": header.bit_count,
                    "contains_icons": header.contains_icons,
                    "display_rate_jiffies": header.display_rate,
                    "flags": header.flags,
                    "frame_count": header.frame_count,
                    "has_sequence": header.has_sequence,
                    "height": header.height,
                    "plane_count": header.plane_count,
                    "step_count": header.step_count,
                    "width": header.width,
                },
                "icon": {
                    "file": f"icons/{asset_name}.png",
                    "frame_id": frame_ids[first_frame_index],
                },
                "jiffies_per_second": JIFFIES_PER_SECOND,
                "loop": {
                    "mode": "infinite",
                    "repeat_count": None,
                    "source_loop_count_encoded": False,
                    "unity_loop_time": True,
                },
                "playback": {
                    "effective_rates_jiffies": list(ani.rates),
                    "effective_sequence": list(ani.sequence),
                    "rate_source": "rate_chunk"
                    if literal_rates is not None
                    else (
                        "header_default"
                        if header is not None and header.display_rate > 0
                        else "fallback_6"
                    ),
                    "sequence_source": "seq_chunk"
                    if literal_sequence is not None
                    else "generated_frame_order",
                    "source_rate_chunk_jiffies": literal_rates,
                    "source_sequence_chunk": literal_sequence,
                },
                "source": _canonical_path(source.relative_to(source_directory)),
                "source_sha256": source_hash,
                "steps": steps,
                "stored_frames": stored_frames,
                "title": ani.title,
                "total_duration_jiffies": start_jiffy,
                "total_duration_ms": round(start_jiffy * 1000 / JIFFIES_PER_SECOND),
                "total_duration_seconds": start_jiffy / JIFFIES_PER_SECOND,
                "union_mask": {
                    "alpha_threshold": alpha_threshold,
                    "file": f"masks/{asset_name}.png",
                    "filled_hole_pixels": filled_hole_pixels,
                    "mode": "RGBA-alpha",
                    "opaque_pixels": opaque_pixels,
                    "sha256": _sha256(union_mask.tobytes()),
                },
                "warnings": warnings,
            }
        )
        total_stored_frames += len(stored_frames)
        total_steps += len(steps)

    ordered_frame_ids = sorted(unique_images)
    atlas_size = _resolve_atlas_size(
        atlas_size_setting, len(ordered_frame_ids), size
    )
    columns = atlas_size // size
    rows_used = math.ceil(len(ordered_frame_ids) / columns)
    # Alpha is intentional in the signature-shell pipeline. Each disconnected
    # shell contains only pixels with the same visibility bit-vector, so Cutout
    # hides or reveals the whole shell instead of punching arbitrary holes into
    # a union plate. Unused atlas cells stay transparent as well.
    atlas_background = Image.new("RGBA", (atlas_size, atlas_size), (0, 0, 0, 0))
    atlas = atlas_background.copy()
    atlas_frames: list[dict[str, Any]] = []

    for frame_index, frame_id in enumerate(ordered_frame_ids):
        x = (frame_index % columns) * size
        y_top = (frame_index // columns) * size
        image = unique_images[frame_id]
        atlas.paste(image, (x, y_top))
        frame_path = frames_directory / f"{frame_id}.png"
        image.save(frame_path, "PNG", optimize=False)
        u_min = x / atlas_size
        v_min = 1 - (y_top + size) / atlas_size
        u_max = (x + size) / atlas_size
        v_max = 1 - y_top / atlas_size
        atlas_frames.append(
            {
                "file": f"frames/{frame_id}.png",
                "frame_id": frame_id,
                "main_tex_st": [u_max - u_min, v_max - v_min, u_min, v_min],
                "pixel_rect_top_left": {
                    "height": size,
                    "width": size,
                    "x": x,
                    "y": y_top,
                },
                "uv_center_unity": [(u_min + u_max) / 2, (v_min + v_max) / 2],
                "uv_rect_unity": {
                    "u_max": u_max,
                    "u_min": u_min,
                    "v_max": v_max,
                    "v_min": v_min,
                },
            }
        )

    atlas.save(output_directory / "atlas.png", "PNG", optimize=False)
    outputs = {
        "atlas": "atlas.png",
        "frames_dir": "frames",
        "icons_dir": "icons",
        "manifest": "manifest.json",
        "masks_dir": "masks",
    }
    atlas_frames_by_id = {frame["frame_id"]: frame for frame in atlas_frames}
    default_candidates = [
        cursor for cursor in cursors if cursor["binding"]["enabled_by_default"]
    ]
    if not default_candidates:
        default_candidates = list(cursors)
    default_cursor = min(
        default_candidates,
        key=lambda cursor: (
            cursor["binding"]["menu_order"],
            cursor["binding"]["parameter_value"],
            cursor["binding"]["asset_name"].casefold(),
        ),
    )
    default_frame_id = str(default_cursor["steps"][0]["frame_id"])
    default_main_tex_st = list(atlas_frames_by_id[default_frame_id]["main_tex_st"])

    manifest: dict[str, Any] = {
        "atlas": {
            "alpha_is_transparency": True,
            "background_rgba": [0, 0, 0, 0],
            "cell_height": size,
            "cell_width": size,
            "columns": columns,
            "file": outputs["atlas"],
            "filter_mode": "point",
            "frames": atlas_frames,
            "height": atlas_size,
            "mipmaps": False,
            "rows_used": rows_used,
            "srgb": True,
            "texture_compression": "uncompressed",
            "width": atlas_size,
            "wrap_mode": "clamp",
        },
        "cursor_count": len(cursors),
        "cursors": cursors,
        "geometry": {
            "alpha_threshold": alpha_threshold,
            "cutoff": alpha_threshold / 255.0,
            "default_frame_id": default_frame_id,
            "default_main_tex_st": default_main_tex_st,
            "mode": "global_alpha_signature_closed_plate",
            "pixel_gap_m": 0.0,
            "signature_basis_frame_ids": ordered_frame_ids,
            "thickness_m": thickness_m,
            "uv_mode": "normalized_frame",
            "world_size_m": world_size_m,
        },
        "generator": {
            "ani_extract_version": ani_extract_version,
            "name": "prepare_ani_cursor.py",
        },
        "image": {
            "alpha_mode": "straight",
            "color_mode": "RGBA",
            "height": size,
            "width": size,
        },
        "outputs": outputs,
        "schema_version": SCHEMA_VERSION,
        "totals": {
            "playback_steps": total_steps,
            "stored_frames": total_stored_frames,
            "unique_frames": len(ordered_frame_ids),
        },
    }
    _write_json(output_directory / "manifest.json", manifest)
    return manifest


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Prepare ANI files as 32 px frames, atlas, icons, masks, and timing JSON."
    )
    parser.add_argument("source_dir", type=Path, help="directory recursively containing ANI files")
    parser.add_argument(
        "--workspace",
        type=Path,
        required=True,
        help="writable staging workspace; relative --output paths are resolved here",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("prepared"),
        help="empty output directory, absolute or relative to --workspace (default: prepared)",
    )
    parser.add_argument("--preset", type=Path, help="optional JSON naming/import preset")
    parser.add_argument(
        "--ani-extract-root",
        type=Path,
        help="ani-extract checkout root; also accepted through ANI_EXTRACT_ROOT",
    )
    parser.add_argument("--pattern", default="*.ani", help="recursive source pattern")
    parser.add_argument("--size", type=int, help="output frame size; default preset value or 32")
    parser.add_argument(
        "--atlas-size",
        help="integer square size, auto power-of-two, or minimal grid",
    )
    parser.add_argument(
        "--alpha-threshold",
        type=int,
        help="union-mask alpha threshold 1..255; default preset value or 1",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        source_directory = args.source_dir.expanduser().resolve()
        workspace = args.workspace.expanduser().resolve()
        workspace.mkdir(parents=True, exist_ok=True)
        output_directory = args.output.expanduser()
        if not output_directory.is_absolute():
            output_directory = workspace / output_directory
        output_directory = output_directory.resolve()
        preset = _read_json(args.preset.expanduser().resolve()) if args.preset else {}
        size = args.size if args.size is not None else int(preset.get("frame_size", 32))
        if size <= 0:
            raise ValueError("Frame size must be positive.")
        atlas_size_setting: str | int = (
            args.atlas_size if args.atlas_size is not None else preset.get("atlas_size", "auto")
        )
        alpha_threshold = (
            args.alpha_threshold
            if args.alpha_threshold is not None
            else int(preset.get("alpha_threshold", 1))
        )
        if not 1 <= alpha_threshold <= 255:
            raise ValueError("Alpha threshold must be between 1 and 255.")

        manifest = prepare(
            source_directory,
            output_directory,
            preset=preset,
            pattern=args.pattern,
            size=size,
            atlas_size_setting=atlas_size_setting,
            alpha_threshold=alpha_threshold,
        )
    except Exception as error:
        print(f"Error: {error}", file=sys.stderr)
        return 1

    totals = manifest["totals"]
    atlas = manifest["atlas"]
    print(
        f"Prepared {manifest['cursor_count']} cursors: {totals['stored_frames']} frames, "
        f"{totals['playback_steps']} steps, {totals['unique_frames']} unique RGBA images."
    )
    print(f"Atlas: {atlas['width']}x{atlas['height']} ({atlas['filter_mode']})")
    print(f"Output: {output_directory}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
