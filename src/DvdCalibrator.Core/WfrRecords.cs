using System.Collections.Immutable;

namespace DvdCalibrator.Core;

public sealed record WfrPoint(int Row, int Ray, double RadiusMm, double AngleRadians, double PhaseRadians,
    double UnwrappedPhaseRadians, double FrequencyCyclesPerPixel, double Amplitude, double Coherence, bool IsHoldout, bool IsRadialHoldout);
public sealed record WfrCenter(string CandidateId, double XMm, double YMm);
public sealed record WfrEvidence(int Channel, int AngularSamples, double CenterShiftLimitMm,
    ImmutableArray<WfrPoint> Points, ImmutableArray<WfrCenter> Centers);
