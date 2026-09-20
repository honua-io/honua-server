// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Attachment metadata follows canonical editing flags, with annotation fallback only
/// for resources without editing metadata (#4825). FeatureServer also requires its store.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Protocol(TestProtocols.MapServer)]
[Collection("Database")]
public sealed class FeatureServerAttachmentCapabilityAdvertisementTests
{
    private const string ServiceName = "attachment-projection";

    private static readonly AttachmentCase[] Cases =
    [
        new(820, true, "honua.io/attachments", "false", true),
        new(821, false, "honua.io/attachments", "true", false),
        new(822, false, "supportsAttachments", "true", false),
        new(823, null, "honua.io/attachments", "true", true),
        new(824, null, "supportsAttachments", "true", true),
        new(825, null, "honua.io/attachments", "false", false),
        new(826, null, null, null, false)
    ];

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task Metadata_CanonicalFlagsOverrideAnnotations_AndLegacyResourcesRemainSupported()
    {
        await using var fixture = await CreateFixtureAsync(withAttachmentStore: true);

        foreach (var item in Cases)
        {
            using var featureLayer = await GetMetadataAsync(fixture, $"FeatureServer/{item.LayerId}");
            AssertFeatureServerFlags(featureLayer.RootElement, item.Expected);

            using var mapLayer = await GetMetadataAsync(fixture, $"MapServer/{item.LayerId}");
            mapLayer.RootElement.GetProperty("hasAttachments").GetBoolean().Should().Be(item.Expected);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServerMetadata_WithoutAttachmentStore_DoesNotAdvertiseAttachmentOperations()
    {
        await using var fixture = await CreateFixtureAsync(withAttachmentStore: false);

        foreach (var item in Cases.Where(item => item.Expected))
        {
            using var featureLayer = await GetMetadataAsync(fixture, $"FeatureServer/{item.LayerId}");
            AssertFeatureServerFlags(featureLayer.RootElement, expected: false);
        }
    }

    private static void AssertFeatureServerFlags(JsonElement layer, bool expected)
    {
        layer.GetProperty("hasAttachments").GetBoolean().Should().Be(expected);
        layer.GetProperty("supportsQueryAttachments").GetBoolean().Should().Be(expected);
        layer.GetProperty("supportsAttachmentKeywords").GetBoolean().Should().Be(expected);
        layer.GetProperty("advancedQueryCapabilities").GetProperty("supportsQueryAttachments")
            .GetBoolean().Should().Be(expected);
        layer.GetProperty("supportsAttachmentsByUploadId").GetBoolean().Should().BeFalse();
    }

    private static async Task<JsonDocument> GetMetadataAsync(WebAppFixture fixture, string suffix)
    {
        using var response = await fixture.Client.GetAsync($"/rest/services/{ServiceName}/{suffix}?f=json");
        response.Be200Ok();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(bool withAttachmentStore)
    {
        var provider = new TestMetadataV2GraphProvider(BuildGraph());
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                services.AddSingleton(provider);
                services.AddSingleton<IMetadataV2GraphProvider>(provider);
                services.AddSingleton<IMetadataV2GraphStore>(provider);
                services.RemoveAll<IAttachmentStore>();
                if (withAttachmentStore)
                {
                    services.AddSingleton<IAttachmentStore, TestAttachmentStore>();
                }
            });
        await fixture.InitializeAsync();
        return fixture;
    }

    private static MetadataV2Graph BuildGraph()
    {
        var anonymous = new AccessPolicy { AllowAnonymous = true };
        var builder = new TestMetadataV2GraphBuilder()
            .AddService("attachment-service", ServiceName,
                protocols: [ServiceProtocols.FeatureServer, ServiceProtocols.MapServer],
                accessPolicy: anonymous);

        foreach (var item in Cases)
        {
            var resourceId = $"attachment-resource-{item.LayerId}";
            var bindingId = $"attachment-binding-{item.LayerId}";
            var annotations = new Dictionary<string, string>();
            if (item.AnnotationKey is not null)
            {
                annotations[item.AnnotationKey] = item.AnnotationValue!;
            }

            builder.AddResource(resourceId, resourceId,
                    fields:
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
                    ],
                    accessPolicy: anonymous,
                    annotations: annotations,
                    spatial: new MetadataV2ResourceSpatial
                    {
                        GeometryType = MetadataV2GeometryType.Point,
                        SpatialReference = new MetadataV2SpatialReference { Srid = 4326, Crs = "EPSG:4326", IsGeographic = true },
                        PrimaryGeometryField = "shape"
                    })
                .AddStorageBinding(bindingId, resourceId, $"test.layers.{item.LayerId}", storageLayerId: item.LayerId)
                .AddPublication($"attachment-publication-{item.LayerId}", "attachment-service", resourceId,
                    layerIndex: item.LayerId, storageBindingId: bindingId,
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer);
        }

        var graph = builder.Build();
        return graph with
        {
            Resources = graph.Resources.Select((resource, index) => resource with
            {
                Editing = Cases[index].TypedFlag is bool enabled
                    ? new MetadataV2ResourceEditing { SupportsAttachments = enabled }
                    : null
            }).ToArray()
        };
    }

    private sealed record AttachmentCase(int LayerId, bool? TypedFlag, string? AnnotationKey, string? AnnotationValue, bool Expected);
}
