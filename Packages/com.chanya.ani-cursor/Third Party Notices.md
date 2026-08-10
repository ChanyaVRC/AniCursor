# Third-Party Notices

ANI Cursor Tool does not bundle the external applications or source projects listed below. They must be obtained separately and remain subject to their respective licenses and terms.

## Runtime build tools

### ani-extract

- Project: [ChanyaVRC/ani-extract](https://github.com/ChanyaVRC/ani-extract)
- Version used for validation: 0.1.0
- Copyright: 2026 Chanya
- License: MIT
- Status: not bundled; a local checkout and its Python environment are selected by the user.

### Pillow

- Project: [python-pillow/Pillow](https://github.com/python-pillow/Pillow)
- License: HPND
- Status: not bundled; installed as a dependency of the selected ani-extract environment.

### Blender

- Project: [Blender](https://www.blender.org/)
- License: GNU General Public License
- Status: not bundled; ANI Cursor Tool starts the user's local Blender executable.

### LogoTracer

- Version used for validation: 1.21
- Status: not bundled; the user supplies the original LogoTracer ZIP. Use and redistribution are governed by the terms supplied by its author.

## Unity and VPM dependencies

The following packages are resolved separately by Unity Package Manager or VPM and are not copied into this package:

- Unity Newtonsoft Json 3.2.1 (`com.unity.nuget.newtonsoft-json`), MIT License.
- VRChat SDK - Avatars 3.10.x, subject to VRChat's terms.
- Modular Avatar 1.18.1 or newer within the declared VPM range, MIT License.
