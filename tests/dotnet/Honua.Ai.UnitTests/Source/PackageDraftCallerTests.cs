// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Owner-subject order for package drafts, and the Redis payload shape. The
/// payload test does not open a Redis connection.
/// </summary>
public sealed class PackageDraftCallerTests
{
    private static readonly DateTimeOffset WrittenAt = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void ResolveOwnerId_UsesNameIdentifierThenSubThenApiKeyThenName()
    {
        PackageDraftCaller.ResolveOwnerId(Principal(ClaimTypes.NameIdentifier, "subject-a", "Ada")).Should().Be("subject-a");
        PackageDraftCaller.ResolveOwnerId(Principal("sub", "subject-from-sub", "Ada")).Should().Be("subject-from-sub");
        PackageDraftCaller.ResolveOwnerId(Principal("api_key_id", "key-1", "Ada")).Should().Be("key-1");

        var nameOnly = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Ada")],
            authenticationType: "Test"));
        PackageDraftCaller.ResolveOwnerId(nameOnly).Should().Be("Ada");
        PackageDraftCaller.ResolveOwnerId(new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"))).Should().BeNull();
    }

    [UnitTest]
    public void RedisPayload_RoundTripsOwnerAndPackage_AndRejectsAForeignVersion()
    {
        var map = new MapPackage
        {
            MapPackageId = "map_alpha",
            Format = "honua_map_package.v1",
            Status = PackageStatus.Draft,
            CreatedAt = WrittenAt,
        };
        var encoded = RedisPackageDraftBacking.Encode(
            PackageDraftKind.Map,
            new PackageDraftRecord("ada", WrittenAt, map, null));

        var decoded = RedisPackageDraftBacking.Decode(PackageDraftKind.Map, encoded);
        decoded.Should().NotBeNull();
        decoded!.OwnerId.Should().Be("ada");
        decoded.WrittenAt.Should().Be(WrittenAt);
        decoded.Map!.MapPackageId.Should().Be("map_alpha");
        decoded.App.Should().BeNull();

        RedisPackageDraftBacking.Decode(PackageDraftKind.Map, "not-json").Should().BeNull();
        using var foreign = JsonDocument.Parse("""{"version":99,"ownerId":"ada","writtenAt":"2026-10-23T18:00:00Z","packageJson":"{}"}""");
        RedisPackageDraftBacking.Decode(PackageDraftKind.Map, foreign.RootElement.GetRawText()).Should().BeNull();
    }

    [UnitTest]
    public void RedisPayload_RoundTripsAnAppDraft()
    {
        var app = new AppPackage
        {
            AppPackageId = "app_alpha",
            TargetSdk = "honua-sdk-js",
            Format = "honua_app_package.v1",
            Status = PackageStatus.Draft,
            CreatedAt = WrittenAt,
        };
        var encoded = RedisPackageDraftBacking.Encode(
            PackageDraftKind.App,
            new PackageDraftRecord("ada", WrittenAt, null, app));

        var decoded = RedisPackageDraftBacking.Decode(PackageDraftKind.App, encoded);
        decoded!.OwnerId.Should().Be("ada");
        decoded.App!.AppPackageId.Should().Be("app_alpha");
        decoded.App.TargetSdk.Should().Be("honua-sdk-js");
    }

    private static ClaimsPrincipal Principal(string claimType, string subject, string name) =>
        new(new ClaimsIdentity(
            [new Claim(claimType, subject), new Claim(ClaimTypes.Name, name)],
            authenticationType: "Test"));
}
