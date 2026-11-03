# Third-party notices

This project is distributed under GPL-3.0-or-later; see LICENSE.

`third_party/DiscImageStudio/DvdTrackGeometry.cs`, `CalibrationPhotoRectifier.cs`,
`ProjectJsonStore.cs` and `ToneProfile.cs` are copied without source changes from
DiscImageStudio, Copyright (c) 2026 Disc Image Studio contributors, GPL-3.0-or-later.
The upstream license notice and license are retained alongside it.
See `upstream.lock.json` for the exact commit, source path and byte hash.

New DvdCalibrator code and documentation: Copyright (c) 2026 DvdCalibrator contributors,
GPL-3.0-or-later. User photos and external standards retain their own rights.

The geometry, rectifier, project JSON loader and its tone dependency are included.
The standalone `OpticalDiscFamily` compatibility enum matches the pinned upstream definition.
Upstream fonts, UI components, CD encoders and
binary packages are not bundled. A future self-contained distribution must also
include the applicable .NET runtime notices and matching source/build instructions.
