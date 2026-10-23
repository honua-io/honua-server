// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Pins the client-verifiable content hash (honua-server#5449) and the publication URL on the
/// publish-request read (honua-server#5788), both through the source-generated (AOT) context.
/// </summary>
public sealed class StudioContentHashInputTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void ComputeInput_Sha256OfInput_IsComputeAndExcludesValidationTimestamp()
    {
        var envelope = Envelope("""{"where":"NAME = 'Café & <Co>'","limit":10}""");
        var input = StudioPackageHash.ComputeInput(envelope);

        Assert.Equal(StudioPackageHash.Compute(envelope), Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
        Assert.Equal(StudioPackageHash.Compute(envelope), StudioPackageHash.ComputeFromInput(input));

        var restamped = envelope with { Validation = envelope.Validation with { GeneratedAt = FixedNow.AddDays(1) } };
        Assert.Equal(input, StudioPackageHash.ComputeInput(restamped));
        using var document = JsonDocument.Parse(input);
        Assert.False(document.RootElement.GetProperty("validation").TryGetProperty("generatedAt", out _));
        Assert.Equal("NAME = 'Café & <Co>'", document.RootElement.GetProperty("body").GetProperty("where").GetString());
    }

    [UnitTest]
    public void TryRebuildInput_ReturnsInputOnlyWhenItStillHashesToTheStoredDigest()
    {
        var envelope = Envelope("""{"where":"1=1","limit":10}""");
        var digest = StudioPackageHash.Compute(envelope);

        var rebuilt = StudioPackageHash.TryRebuildInput(envelope, digest);
        Assert.NotNull(rebuilt);
        Assert.Equal(digest, StudioPackageHash.ComputeFromInput(Convert.FromBase64String(rebuilt!)));

        // jsonb reorders object keys: the same content read back no longer reproduces the bytes
        // that were hashed, so no input is offered rather than one that fails to recompute.
        var reordered = Envelope("""{"limit":10,"where":"1=1"}""");
        Assert.Null(StudioPackageHash.TryRebuildInput(reordered, digest));
    }

    [UnitTest]
    public void ContentVersion_Serialization_CarriesContentHashInputOnlyWhenPresent()
    {
        var envelope = Envelope("""{"where":"1=1"}""");
        var input = StudioPackageHash.ComputeInput(envelope);
        var version = new StudioContentVersion
        {
            ItemId = Guid.NewGuid(),
            PackageKey = "parcels",
            VersionId = Guid.NewGuid(),
            VersionNumber = 1,
            ContentHash = StudioPackageHash.ComputeFromInput(input),
            ContentHashInput = Convert.ToBase64String(input),
            Envelope = envelope,
            Validation = envelope.Validation,
            CreatedAt = FixedNow,
        };

        using var withInput = JsonDocument.Parse(JsonSerializer.Serialize(version, StudioJsonContext.Default.StudioContentVersion));
        var encoded = withInput.RootElement.GetProperty("contentHashInput").GetString();
        Assert.Equal(
            withInput.RootElement.GetProperty("contentHash").GetString(),
            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(encoded!))).ToLowerInvariant());

        var roundTripped = JsonSerializer.Deserialize(withInput.RootElement.GetRawText(), StudioJsonContext.Default.StudioContentVersion);
        Assert.Equal(version.ContentHashInput, roundTripped!.ContentHashInput);

        using var withoutInput = JsonDocument.Parse(JsonSerializer.Serialize(
            version with { ContentHashInput = null },
            StudioJsonContext.Default.StudioContentVersion));
        Assert.False(withoutInput.RootElement.TryGetProperty("contentHashInput", out _));
    }

    [UnitTest]
    public void PublicationRequest_Serialization_CarriesPublicationUrlOnlyWhenAccepted()
    {
        const string route = "/maps/parcels v2";
        var accepted = Request(StudioPublicationRequestStatus.Accepted, route);
        Assert.Equal(StudioPublishedRoutes.BuildActiveUrl(route), accepted.PublicationUrl);

        using var acceptedJson = JsonDocument.Parse(JsonSerializer.Serialize(accepted, StudioJsonContext.Default.StudioPublicationRequest));
        Assert.Equal(
            "/api/v1/studio/published/maps/parcels%20v2",
            acceptedJson.RootElement.GetProperty("publicationUrl").GetString());

        // The URL is derived, never stored: a persisted payload that carries it reads back cleanly
        // and the value still follows the status and route.
        var roundTripped = JsonSerializer.Deserialize(acceptedJson.RootElement.GetRawText(), StudioJsonContext.Default.StudioPublicationRequest);
        Assert.Equal(accepted.PublicationUrl, roundTripped!.PublicationUrl);

        foreach (var request in new[]
                 {
                     Request(StudioPublicationRequestStatus.Pending, route),
                     Request(StudioPublicationRequestStatus.Rejected, route),
                     Request(StudioPublicationRequestStatus.Accepted, route: null),
                 })
        {
            Assert.Null(request.PublicationUrl);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, StudioJsonContext.Default.StudioPublicationRequest));
            Assert.False(json.RootElement.TryGetProperty("publicationUrl", out _));
        }
    }

    private static StudioPublicationRequest Request(StudioPublicationRequestStatus status, string? route) => new()
    {
        RequestId = Guid.NewGuid(),
        ItemId = Guid.NewGuid(),
        VersionId = Guid.NewGuid(),
        Intent = route is null ? null : new StudioPublicationIntent { Route = route, Visibility = "public" },
        Status = status,
        CreatedAt = FixedNow,
    };

    private static StudioPackageEnvelope Envelope(string body)
    {
        using var document = JsonDocument.Parse(body);
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Query,
            SchemaVersion = "1.0",
            Format = "studio_query_package.v1",
            Validation = StudioValidationSummary.NotValidated with { GeneratedAt = FixedNow },
            Body = document.RootElement.Clone(),
        };
    }
}
