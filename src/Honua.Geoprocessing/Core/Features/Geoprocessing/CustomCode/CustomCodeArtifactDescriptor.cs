// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Geoprocessing.CustomCode;

public sealed record CustomCodeArtifactDescriptor
{
    public string Schema { get; init; } = "honua.gp.custom-artifact/v1";
    public required string JobId { get; init; }
    public required int Attempt { get; init; }
    public required string Name { get; init; }
    // The object identity is chosen by the server and is never accepted from a caller.
    public required string ObjectKey { get; init; }
    public CloudStorageProvider Provider { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public int UploadAttempts { get; init; } = 1;
    public string State { get; init; } = "pending";

    public string ContentUrl => $"/api/geoprocessing/jobs/{Uri.EscapeDataString(JobId)}/custom-artifacts/{Uri.EscapeDataString(Name)}?attempt={Attempt}";

    public string Serialize() => JsonSerializer.Serialize(this, CustomCodeArtifactJsonContext.Default.CustomCodeArtifactDescriptor);

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

public interface ICustomCodeArtifactIngestor
{
    Task IngestAsync(ExecutionJobRecord job, CustomCodeArtifactDescriptor artifact, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CustomCodeArtifactDescriptor))]
internal sealed partial class CustomCodeArtifactJsonContext : JsonSerializerContext
{
}
