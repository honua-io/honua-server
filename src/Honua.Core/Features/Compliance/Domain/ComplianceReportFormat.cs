// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public enum ComplianceReportFormat
{
    Csv = 0,

    Pdf = 1,
}

public sealed record ComplianceReportArtifact
{
    public required ComplianceReportFormat Format { get; init; }

    public required string ContentType { get; init; }

    public required string FileExtension { get; init; }

    public required string FileNameStem { get; init; }

    public required byte[] Content { get; init; }
}
