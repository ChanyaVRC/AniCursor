# Changelog

All notable changes to this package are documented in this file.

## [1.0.2] - 2026-08-10

### Changed

- Meter units and Unity axis conversion are now baked into generated FBX mesh data.
- Generated and migrated `CursorDisplay` objects now use an identity local transform: Position `0`, Rotation `0`, Scale `1`.
- Added the standalone VPM repository layout and GitHub Pages listing automation.

## [1.0.1] - 2026-08-10

### Changed

- Generated and migrated `CursorDisplay` objects now always start at local position `(0, 0, 0)` while preserving rotation and scale.

## [1.0.0] - 2026-08-10

### Added

- Windows ANI cursor extraction with original playback order and timing.
- Pixel-shaped, closed 3D cursor mesh generation through Blender and LogoTracer.
- Point-filtered atlas, menu icons, animation clips, controller, material, FBX, and prefab generation.
- Modular Avatar menu, parameter, animator, and RightHand Bone Proxy integration.
- Reusable preset support for named cursor sets.
