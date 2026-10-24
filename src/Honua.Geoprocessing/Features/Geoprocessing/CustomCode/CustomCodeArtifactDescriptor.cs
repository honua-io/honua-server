// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>Server-owned receipt for an immutable, attempt-bound custom-code output.</summary>
public sealed record CustomCodeArtifactDescriptor
{
    /// <summary>Descriptor discriminator.</summary>
    public string Schema { get; init; } = "honua.gp.custom-artifact/v1";
    /// <summary>Owning durable job.</summary>
    public required string JobId { get; init; }
    /// <summary>Producing dispatch attempt.</summary>
    public required int Attempt { get; init; }
    /// <summary>Safe terminal file name.</summary>
    public required string Name { get; init; }
    /// <summary>Server-selected cloud object identity; never accepted from a caller.</summary>
    public required string ObjectKey { get; init; }
    /// <summary>Cloud provider that owns the immutable object; mismatched providers fail closed.</summary>
    public CloudStorageProvider Provider { get; init; }
    /// <summary>Uploaded content type.</summary>
    public required string ContentType { get; init; }
    /// <summary>Reserved and then verified byte length.</summary>
    public required long SizeBytes { get; init; }
    /// <summary>SHA-256 computed by the broker, absent until upload completes.</summary>
    public string? Sha256 { get; init; }
    /// <summary>CAS-counted bounded upload attempts charged to this reservation.</summary>
    public int UploadAttempts { get; init; } = 1;
    /// <summary>Pending reservation or available immutable object.</summary>
    public string State { get; init; } = "pending";

    /// <summary>Authenticated same-origin download route.</summary>
    public string ContentUrl => $"/api/geoprocessing/jobs/{Uri.EscapeDataString(JobId)}/custom-artifacts/{Uri.EscapeDataString(Name)}?attempt={Attempt}";

    /// <summary>Serializes the receipt into the canonical job artifact-reference list.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, CustomCodeArtifactJsonContext.Default.CustomCodeArtifactDescriptor);

    /// <summary>Recognizes only this typed receipt; arbitrary provider references stay untouched.</summary>
    public static bool TryParse(string reference, out CustomCodeArtifactDescriptor? descriptor)
    {
        descriptor = null;
        if (!reference.StartsWith('{') || !reference.Contains("honua.gp.custom-artifact/v1", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            descriptor = JsonSerializer.Deserialize(reference, CustomCodeArtifactJsonContext.Default.CustomCodeArtifactDescriptor);
            return descriptor is { Schema: "honua.gp.custom-artifact/v1", Attempt: > 0, SizeBytes: >= 0 }
                && descriptor.State is "pending" or "available";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Ingests a durable immutable harness journal into canonical job diagnostics.</summary>
public interface ICustomCodeArtifactIngestor
{
    /// <summary>Idempotently ingests this job and attempt's journal, with bounded parsing and redaction.</summary>
    Task IngestAsync(ExecutionJobRecord job, CustomCodeArtifactDescriptor artifact, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CustomCodeArtifactDescriptor))]
internal sealed partial class CustomCodeArtifactJsonContext : JsonSerializerContext
{
}
