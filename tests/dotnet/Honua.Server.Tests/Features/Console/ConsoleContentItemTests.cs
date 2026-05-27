// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Console.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Verifies the wire shape of the Console content item DTO so SDK generation
/// stays consistent with documented OpenAPI behaviour.
/// </summary>
public class ConsoleContentItemTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [UnitTest]
    public void ConsoleContentItem_RoundTripsThroughSourceGenContext()
    {
        using var typeMetadata = JsonDocument.Parse("""{"resourceId":"res-1","fieldCount":4}""");
        var item = new ConsoleContentItem
        {
            Id = "item-1",
            Name = "parcels",
            Namespace = "ns",
            Title = "Parcels",
            Description = "Parcel layer",
            ItemType = ConsoleContentItemType.Layer,
            Tags = new[] { "parcels", "cadastre" },
            Labels = new Dictionary<string, string> { ["env"] = "prod" },
            Lifecycle = MetadataV2LifecycleStatus.Active,
            OperationalState = MetadataV2OperationalState.Ready,
            Visibility = ConsoleVisibility.Organization,
            OwnerId = "owner",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Generation = 7,
            Actions = new[] { ConsoleContentAction.View, ConsoleContentAction.Edit },
            Provenance = new[]
            {
                new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = "res-1", Rel = "derived-from" },
            },
            TypeMetadata = typeMetadata.RootElement.Clone(),
        };

        var json = JsonSerializer.Serialize(item, ConsoleJsonContext.Default.ConsoleContentItem);

        Assert.Contains("\"itemType\":\"layer\"", json);
        Assert.Contains("\"lifecycle\":\"active\"", json);
        Assert.Contains("\"visibility\":\"organization\"", json);
        Assert.Contains("\"actions\":[\"view\",\"edit\"]", json);
        Assert.Contains("\"resourceId\":\"res-1\"", json);

        var roundTrip = JsonSerializer.Deserialize(json, ConsoleJsonContext.Default.ConsoleContentItem)!;
        Assert.Equal(item.Id, roundTrip.Id);
        Assert.Equal(item.ItemType, roundTrip.ItemType);
        Assert.Equal(item.Visibility, roundTrip.Visibility);
        Assert.Equal(item.Generation, roundTrip.Generation);
        Assert.NotNull(roundTrip.TypeMetadata);
        Assert.Equal(4, roundTrip.TypeMetadata!.Value.GetProperty("fieldCount").GetInt32());
    }

    [UnitTest]
    public void ConsoleProvenanceRef_OmitsNullNamespaceField()
    {
        var pref = new ConsoleProvenanceRef { Kind = "studio-artifact", ItemId = "art-1", Rel = "generated-by" };

        var json = JsonSerializer.Serialize(pref, ConsoleJsonContext.Default.ConsoleProvenanceRef);

        Assert.DoesNotContain("\"namespace\"", json);
        Assert.Contains("\"kind\":\"studio-artifact\"", json);
        Assert.Contains("\"rel\":\"generated-by\"", json);
    }

    [UnitTest]
    public void ConsoleSessionContext_IncludesUserCapabilitiesEntitlementsAndContent()
    {
        var session = new ConsoleSessionContext
        {
            User = new ConsoleUserProfile { Id = "u1", Name = "Operator", Email = "op@example.com" },
            Capabilities = new[] { "catalog.read", "studio.edit" },
            NavigationEntitlements = new[]
            {
                new ConsoleNavigationEntitlement { RouteKey = "studio", Allowed = true },
                new ConsoleNavigationEntitlement { RouteKey = "admin", Allowed = false, Reason = "insufficient-capability" },
            },
            Content = new ConsoleContentPage
            {
                Items = Array.Empty<ConsoleContentItem>(),
                Total = 0,
            },
        };

        var json = JsonSerializer.Serialize(session, ConsoleJsonContext.Default.ConsoleSessionContext);

        Assert.Contains("\"capabilities\":[\"catalog.read\",\"studio.edit\"]", json);
        Assert.Contains("\"navigationEntitlements\":[", json);
        Assert.Contains("\"allowed\":true", json);
        Assert.Contains("\"reason\":\"insufficient-capability\"", json);
        Assert.Contains("\"content\":{", json);
    }
}
