# Third-party notices

This project is distributed under GPL-3.0-or-later; see LICENSE.

`third_party/DiscImageStudio/DvdTrackGeometry.cs`, `CalibrationPhotoRectifier.cs`,
`ProjectJsonStore.cs`, `ToneProfile.cs` and `CalibrationReferencePattern.cs` are copied without source changes from
DiscImageStudio, Copyright (c) 2026 Disc Image Studio contributors, GPL-3.0-or-later.
The upstream license notice and license are retained alongside it.
See `upstream.lock.json` for the exact commit, source path and byte hash.

New DvdCalibrator code and documentation: Copyright (c) 2026 DvdCalibrator contributors,
GPL-3.0-or-later. User photos and external standards retain their own rights.

The geometry, rectifier, project JSON loader, tone dependency and reference-pattern sampler are included.
The unchanged `target_slim.png` and its `target_slim.gray.gz` pixel resource are copied from the same
pinned upstream tree; their original hashes and resource identities are retained. They are not redrawn.
The standalone `OpticalDiscFamily` and `CalibrationDiscKind` compatibility enums match the pinned upstream definitions.
Upstream fonts, UI components, CD encoders and
binary packages are not bundled. A future self-contained distribution must also
include the applicable .NET runtime notices and matching source/build instructions.
