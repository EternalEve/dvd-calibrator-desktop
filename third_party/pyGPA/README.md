# pyGPA reference

Source: https://github.com/TAdeJong/pyGPA/blob/main/pyGPA/geometric_phase_analysis.py

Retrieved 2026-11-04. SHA-256 of the unchanged reference file:
`37529df57afb02ed4538f2a2e41a0ef30c7228c05bc64b6708a118f0a29c8024`.

`WindowedFourierRidges.cs` adapts `GPA` and `wfr3` to radial carriers in a rectified polar image. Math.NET supplies the transforms. Baseline removal, observation screening, independent validation and DVD geometry fitting are integration code in this project. `fixtures/pygpa-wfr-oracle.json` records numerical responses from the reference implementation. The original Python source is retained for attribution and comparison; the application does not execute it.

Copyright (c) 2021 Tobias A. de Jong. MIT; see LICENSE.
