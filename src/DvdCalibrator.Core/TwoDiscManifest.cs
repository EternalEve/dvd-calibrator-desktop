namespace DvdCalibrator.Core;

public sealed record TwoDiscManifest(int SchemaVersion, string RecordType, string Id, string EvidenceMode,
    FineTargetPlan Plan, string CoarseTaskSha256, string? FineTaskSha256, string PhysicalValidation);
public sealed record TwoDiscConsistency(string Status, double CoarseShapeRmsMm, double CoarseShapeMaximumMm,
    double FineSourcePixelsPerMm, double FineLineWidthPixels);
