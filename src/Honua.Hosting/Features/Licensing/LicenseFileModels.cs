// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Licensing;

internal sealed class SignedLicenseEnvelope
{
    public int Version { get; init; }

    public string? KeyId { get; init; }

    public string? Payload { get; init; }

    public string? Signature { get; init; }
}

internal sealed class SignedLicensePayload
{
    public string? Schema { get; init; }

    public string? LicenseId { get; init; }

    public string? LicensedTo { get; init; }

    public string? Edition { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string[]? Entitlements { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
}

internal sealed class LicenseHealthSummary
{
    public required string Edition { get; init; }

    public required string ValidationState { get; init; }

    public required bool IsValid { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public int? DaysUntilExpiry { get; init; }

    public string? LicenseId { get; init; }

    public string? LicensedTo { get; init; }

    public required string[] ActiveEntitlements { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SignedLicenseEnvelope))]
[JsonSerializable(typeof(SignedLicensePayload))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(LicenseHealthSummary))]
internal sealed partial class LicenseFileJsonContext : JsonSerializerContext
{
}
