// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Tiles;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

/// <summary>
/// Focused tests for the protocol-neutral tile-export lifecycle service: submission,
/// idempotency, admission, ownership/binding isolation, cancellation, and result delivery.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed partial class TileExportJobServiceTests
{
    private const string Owner = "user-alice";
    private const string Other = "user-bob";

    private static ClaimsPrincipal ApiKeyPrincipal(string id)
        => new(new ClaimsIdentity([new Claim("api_key_id", id), new Claim(ClaimTypes.Name, "shared-name")], AuthenticationExtensions.ApiKeyScheme));

    private static ClaimsPrincipal TenantPrincipal(string id, string? tenant)
    {
        var principal = Principal(id);
        if (tenant is not null) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("tenant_id", tenant));
        return principal;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TileExportJobService CreateService(
        InMemoryExecutionJobStore store,
        InMemoryJobQueue queue,
        ICloudFileStorage? storage = null,
        IExecutionAdmissionEvaluator? admission = null)
        => new(
            TimeProvider.System,
            StorageOptions(),
            NullLogger<TileExportJobService>.Instance,
            store,
            queue,
            storage,
            admission);

    private static IOptions<CloudStorageOptions> StorageOptions()
        => Options.Create(new CloudStorageOptions());

    private static async Task MarkSucceededAsync(InMemoryExecutionJobStore store, string jobId, string artifactReference)
    {
        var job = (await store.GetAsync(jobId))!;
        await store.SetAsync(job with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = [artifactReference]
        });
    }

    private static TileExportJobScope ScopeFor(TileExportJobPlan plan)
        => new(plan.SourceKind, plan.ResourceId);

    private static ClaimsPrincipal Principal(string? id, params string[] roles)
    {
        var claims = new List<Claim>();
        if (id is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static CloudFile StoredArtifact(string key, DateTimeOffset expiresAt, long sizeBytes)
        => new()
        {
            FileId = key,
            FileName = Path.GetFileName(key),
            StoragePath = key,
            ContentType = "application/octet-stream",
            SizeBytes = sizeBytes,
            UploadedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = expiresAt,
            Provider = CloudStorageProvider.Local
        };

    private static TileExportJobPlan CreatePlan()
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = "world-basemap",
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                "metadata-v2-publication-tenant-42",
                null),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };

    private static TileExportJobPlan CreateRasterPlan(string resourceId)
        => new()
        {
            SourceKind = TileExportSourceKind.Raster,
            ResourceId = resourceId,
            Source = new TileExportRasterSourceDescriptor(
                42,
                "7",
                "First",
                null,
                "same-membership"),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };
}
