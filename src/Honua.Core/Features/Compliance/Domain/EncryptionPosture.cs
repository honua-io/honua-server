// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

// Key-version fields are an auditor timeline and are not cipher key material.
public sealed record EncryptionPosture
{
    // FIPS mode comes from the runtime FIPS flag and the OS FIPS indicator, not from configuration.
    public required bool FipsMode { get; init; }

    public required string FipsSource { get; init; }

    public required IReadOnlyList<string> Algorithms { get; init; }

    public required int ActiveKeyVersion { get; init; }

    public required int RetainedKeyVersions { get; init; }

    public DateTimeOffset? ActiveKeyIssuedAt { get; init; }

    public DateTimeOffset? LastRotationAt { get; init; }
}
