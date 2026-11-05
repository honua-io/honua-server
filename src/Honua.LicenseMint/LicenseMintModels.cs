// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.LicenseMint;

// Field names, camelCase, and null omission stay byte-identical to the verifier, because Ed25519 signs the exact payload bytes.
internal sealed class LicenseMintPayload
{
    public string? Schema { get; init; }

    public string? LicenseId { get; init; }

    public string? LicensedTo { get; init; }

    public string? Edition { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string[]? Entitlements { get; init; }

    public LicenseCapacityTerms? Capacity { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
}

internal sealed class LicenseMintEnvelope
{
    public int Version { get; init; }

    public string? KeyId { get; init; }

    public string? Payload { get; init; }

    public string? Signature { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LicenseMintPayload))]
[JsonSerializable(typeof(LicenseMintEnvelope))]
[JsonSerializable(typeof(LicenseCapacityTerms))]
internal sealed partial class LicenseMintJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
