// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OgcGeoJsonFeature = Honua.Server.Features.OgcFeatures.Models.GeoJsonFeature;

namespace Honua.Server.Tests.Features.Security;

public sealed class FeatureServerServiceRbacTests
{
    private const string CalculateExpression = "[{\"field\":\"name\",\"sqlExpression\":\"'RBAC'\"}]";

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithReadOnlyRole_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithScopedDataEditor_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithScopedDataEditor_DeniesOtherService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.BetaService}/FeatureServer/{ServiceRbacTestFixture.BetaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithAdminRole_AllowsAnyService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.BetaService}/FeatureServer/{ServiceRbacTestFixture.BetaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_WithAnonymousClient_ReturnsUnauthorized()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/calculate?calcExpression={Uri.EscapeDataString(CalculateExpression)}&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_WithReadOnlyRole_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/calculate?calcExpression={Uri.EscapeDataString(CalculateExpression)}&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_WithScopedDataEditor_DeniesOtherService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.BetaService}/FeatureServer/{ServiceRbacTestFixture.BetaLayerId}/calculate?calcExpression={Uri.EscapeDataString(CalculateExpression)}&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_WithScopedDataEditor_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/calculate?calcExpression={Uri.EscapeDataString(CalculateExpression)}&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithReadOnlyRole_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "rbac-replica",
            layers = ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_WithReadOnlyRole_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var adminClient = ServiceRbacTestFixture.CreateClient(factory, "admin");
        using var readerClient = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "rbac-sync",
            layers = ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });
        var createResponse = await adminClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));
        await ServiceRbacTestFixture.AssertStatusAsync(createResponse, HttpStatusCode.OK);

        var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var replicaId = createDocument.RootElement.GetProperty("replicaID").GetString();
        replicaId.Should().NotBeNullOrWhiteSpace();

        var syncPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });
        var syncResponse = await readerClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/synchronizeReplica",
            new StringContent(syncPayload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(syncResponse, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.FeatureServer)]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_WithReadOnlyRole_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var adminClient = ServiceRbacTestFixture.CreateClient(factory, "admin");
        using var readerClient = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var createPayload = JsonSerializer.Serialize(new
        {
            replicaName = "rbac-unregister",
            layers = ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });
        var createResponse = await adminClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));
        await ServiceRbacTestFixture.AssertStatusAsync(createResponse, HttpStatusCode.OK);

        var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var replicaId = createDocument.RootElement.GetProperty("replicaID").GetString();
        replicaId.Should().NotBeNullOrWhiteSpace();

        var unregisterPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });
        var unregisterResponse = await readerClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/unRegisterReplica",
            new StringContent(unregisterPayload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(unregisterResponse, HttpStatusCode.Forbidden);
    }
}

public sealed class OgcServiceRbacTests
{
    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithScopedDataEditor_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithScopedDataEditor_DeniesOtherService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.BetaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithAdminRole_AllowsAnyService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.BetaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }
}

public sealed class OgcServiceAccessPolicyTests
{
    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithAdminRole_RespectsServiceWritePolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                betaServiceMetadata: new CatalogMetadata
                {
                    AccessPolicy = new AccessPolicy
                    {
                        AllowedWriteRoles = ["beta-writer"]
                    }
                }));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.BetaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }
}

public sealed class ODataServiceRbacTests
{
    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithScopedDataEditor_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithScopedDataEditor_DeniesOtherService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.BetaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithAdminRole_AllowsAnyService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.BetaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithScopedDataEditor_DeniesOtherService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "create-1",
                Method = "POST",
                Url = $"Layers({ServiceRbacTestFixture.BetaLayerId})/Features",
                Body = new Dictionary<string, object?>
                {
                    ["Attributes"] = new Dictionary<string, object?>
                    {
                        ["name"] = "RBAC Batch"
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features({layerId})?$search")]
    public async Task Search_WithAnonymousClient_ReturnsUnauthorized()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/odata/Features({ServiceRbacTestFixture.AlphaLayerId})?$search=RBAC");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.ODataApply)]
    [Endpoint("GET /odata/Features({layerId})?$apply")]
    public async Task Apply_WithAnonymousClient_ReturnsUnauthorized()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/odata/Features({ServiceRbacTestFixture.AlphaLayerId})?$apply=aggregate(ObjectId with countdistinct as Total)");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.ODataSearch)]
    [Endpoint("GET /odata/Features?$search")]
    public async Task Search_AllLayersRoute_WithAnonymousClient_ReturnsUnauthorized()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/odata/Features?$search=RBAC&$filter=LayerId eq {ServiceRbacTestFixture.AlphaLayerId}");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }
}

public sealed class ODataServiceAccessPolicyTests
{
    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithAdminRole_RespectsServiceWritePolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                betaServiceMetadata: new CatalogMetadata
                {
                    AccessPolicy = new AccessPolicy
                    {
                        AllowedWriteRoles = ["beta-writer"]
                    }
                }));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.BetaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithAdminRole_RespectsServiceWritePolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                betaServiceMetadata: new CatalogMetadata
                {
                    AccessPolicy = new AccessPolicy
                    {
                        AllowedWriteRoles = ["beta-writer"]
                    }
                }));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "create-beta",
                Method = "POST",
                Url = $"Layers({ServiceRbacTestFixture.BetaLayerId})/Features",
                Body = new Dictionary<string, object?>
                {
                    ["Attributes"] = new Dictionary<string, object?>
                    {
                        ["name"] = "Policy-protected write"
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }
}

public sealed class ODataServiceBoundaryTests
{
    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers")]
    public async Task GetLayers_WithSharedLayerInSecondaryService_DoesNotLeakCanonicalBoundary()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"]),
                betaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["beta-reader"]),
                betaAlsoIncludesAlphaLayer: true,
                reverseServiceOrder: true));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "beta-reader");

        var response = await client.GetAsync("/odata/Layers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var layers = ServiceRbacTestFixture.GetPropertyCaseInsensitive(json.RootElement, "value");
        layers.EnumerateArray()
            .Select(layer => ServiceRbacTestFixture.GetPropertyCaseInsensitive(layer, "Id").GetInt32())
            .Should()
            .NotContain(ServiceRbacTestFixture.AlphaLayerId);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithSecondaryScopedEditor_DeniesCanonicalServiceMismatch()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(betaAlsoIncludesAlphaLayer: true, reverseServiceOrder: true));
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.BetaService}");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_PublishesCanonicalServiceId()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");
        var store = factory.Services.GetRequiredService<IFeatureChangeEventStore>();

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);

        var events = await store.QueryAsync(cursor: null, from: null, to: null, limit: 10);
        events.Should().NotBeEmpty();
        events[^1].ServiceId.Should().Be(ServiceRbacTestFixture.AlphaService);
    }

    [IntegrationTest]
    [Protocol(Protocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task BatchCreateFeature_PublishesCanonicalServiceId()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");
        var store = factory.Services.GetRequiredService<IFeatureChangeEventStore>();

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "create-alpha",
                Method = "POST",
                Url = $"Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
                Body = new Dictionary<string, object?>
                {
                    ["Attributes"] = new Dictionary<string, object?>
                    {
                        ["name"] = "Batch Feature"
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        var events = await store.QueryAsync(cursor: null, from: null, to: null, limit: 10);
        events.Should().NotBeEmpty();
        events[^1].ServiceId.Should().Be(ServiceRbacTestFixture.AlphaService);
    }
}

public sealed class OgcServiceBoundaryTests
{
    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections")]
    public async Task GetCollections_WithSharedLayerInSecondaryService_DoesNotLeakCanonicalBoundary()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"]),
                betaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["beta-reader"]),
                betaAlsoIncludesAlphaLayer: true,
                reverseServiceOrder: true));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "beta-reader");

        var response = await client.GetAsync("/ogc/features/collections");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var collections = ServiceRbacTestFixture.GetPropertyCaseInsensitive(json.RootElement, "collections");
        collections.EnumerateArray()
            .Select(collection => ServiceRbacTestFixture.GetPropertyCaseInsensitive(collection, "id").GetString())
            .Should()
            .NotContain(ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture));
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithSecondaryScopedEditor_DeniesCanonicalServiceMismatch()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(betaAlsoIncludesAlphaLayer: true, reverseServiceOrder: true));
        using var client = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.BetaService}");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(Protocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_PublishesCanonicalServiceId()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");
        var store = factory.Services.GetRequiredService<IFeatureChangeEventStore>();

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);

        var events = await store.QueryAsync(cursor: null, from: null, to: null, limit: 10);
        events.Should().NotBeEmpty();
        events[^1].ServiceId.Should().Be(ServiceRbacTestFixture.AlphaService);
    }
}

internal static class ServiceRbacTestFixture
{
    public const string AlphaService = "alpha";
    public const string BetaService = "beta";
    public const int AlphaLayerId = 0;
    public const int BetaLayerId = 1;

    public static WebApplicationFactory<Program> CreateFactory(Func<ILayerCatalog>? layerCatalogFactory = null)
    {
        layerCatalogFactory ??= static () => new RbacTestLayerCatalog();

        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Rbac:RoleClaimType"] = "roles",
                        ["Rbac:DataEditorServicePrefix"] = "data-editor:",
                        ["Limits:Validation:EnableTopologyValidation"] = "false",
                        ["Limits:Validation:EnableAutoRepair"] = "false",
                        ["Limits:Validation:Mode"] = "Accept"
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ILayerCatalog>();
                    services.AddScoped<ILayerCatalog>(_ => layerCatalogFactory());
                    services.AddSingleton<ICrsRegistry, TestCrsRegistry>();
                    services.AddSingleton<ICoordinateTransformService, TestCoordinateTransformService>();
                    services.AddSingleton<IGeometryTopologyValidator, NoOpGeometryTopologyValidator>();

                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                    });
                });
            });
    }

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "rbac-user");

        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        }

        return client;
    }

    public static StringContent CreateApplyEditsContent()
    {
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "RBAC Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public static CatalogMetadata CreateServiceMetadata(
        string[]? readRoles = null,
        string[]? writeRoles = null)
    {
        return new CatalogMetadata
        {
            AccessPolicy = new AccessPolicy
            {
                AllowedRoles = readRoles,
                AllowedWriteRoles = writeRoles
            }
        };
    }

    public static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
    }

    public static StringContent CreateOgcFeatureContent()
    {
        var feature = new OgcGeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "RBAC Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        return new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue.Parse(MediaTypes.GeoJson));
    }

    public static StringContent CreateODataFeatureContent()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "RBAC Feature"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Test";
    internal const string UserHeader = "X-Test-User";
    internal const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var userName = userValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, userName) };

        if (Request.Headers.TryGetValue(RolesHeader, out var rolesValues))
        {
            var roleTokens = rolesValues.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var role in roleTokens)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("roles", role));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class RbacTestLayerCatalog : ILayerCatalog
{
    private static readonly string[] _supportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _capabilities = ["Query", "Create", "Update", "Delete"];

    private readonly ServiceDefinition _alphaService;
    private readonly ServiceDefinition _betaService;
    private readonly ServiceDefinition[] _services;
    private readonly LayerDefinition _alphaLayer;
    private readonly LayerDefinition _betaLayer;

    public RbacTestLayerCatalog(
        CatalogMetadata? alphaServiceMetadata = null,
        CatalogMetadata? betaServiceMetadata = null,
        CatalogMetadata? alphaLayerMetadata = null,
        CatalogMetadata? betaLayerMetadata = null,
        bool betaAlsoIncludesAlphaLayer = false,
        bool reverseServiceOrder = false)
    {
        var spatialRef = SpatialReference.Create(4326);
        var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);

        _alphaLayer = CreateLayer(ServiceRbacTestFixture.AlphaLayerId, "Alpha Layer", spatialRef, extent, alphaLayerMetadata);
        _betaLayer = CreateLayer(ServiceRbacTestFixture.BetaLayerId, "Beta Layer", spatialRef, extent, betaLayerMetadata);
        var alphaLayers = new[] { _alphaLayer };
        var betaLayers = betaAlsoIncludesAlphaLayer
            ? new[] { _alphaLayer, _betaLayer }
            : new[] { _betaLayer };

        _alphaService = new ServiceDefinition(
            Name: ServiceRbacTestFixture.AlphaService,
            Description: "Alpha service for RBAC tests",
            Layers: alphaLayers,
            SpatialReference: spatialRef,
            SupportedFormats: _supportedFormats,
            Capabilities: _capabilities,
            ServiceExtent: extent,
            Metadata: alphaServiceMetadata);

        _betaService = new ServiceDefinition(
            Name: ServiceRbacTestFixture.BetaService,
            Description: "Beta service for RBAC tests",
            Layers: betaLayers,
            SpatialReference: spatialRef,
            SupportedFormats: _supportedFormats,
            Capabilities: _capabilities,
            ServiceExtent: extent,
            Metadata: betaServiceMetadata);

        _services = reverseServiceOrder
            ? [_betaService, _alphaService]
            : [_alphaService, _betaService];
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(layerId switch
        {
            ServiceRbacTestFixture.AlphaLayerId => _alphaLayer,
            ServiceRbacTestFixture.BetaLayerId => _betaLayer,
            _ => null
        });
    }

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new[] { _alphaLayer, _betaLayer });

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.Equals(serviceName, ServiceRbacTestFixture.AlphaService, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ServiceDefinition?>(_alphaService);
        }

        if (string.Equals(serviceName, ServiceRbacTestFixture.BetaService, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ServiceDefinition?>(_betaService);
        }

        return Task.FromResult<ServiceDefinition?>(null);
    }

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_services);

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(layerId == ServiceRbacTestFixture.AlphaLayerId || layerId == ServiceRbacTestFixture.BetaLayerId);

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(
            string.Equals(serviceName, ServiceRbacTestFixture.AlphaService, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(serviceName, ServiceRbacTestFixture.BetaService, StringComparison.OrdinalIgnoreCase));

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<Relationship>());

    private static LayerDefinition CreateLayer(
        int layerId,
        string name,
        SpatialReference spatialRef,
        FeatureExtent extent,
        CatalogMetadata? metadata = null)
    {
        var fields = new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, null, false, null, "Object ID"),
            new FieldDefinition("name", FieldType.String, 255, true, null, "Name field")
        };

        return new LayerDefinition(
            Id: layerId,
            Name: name,
            Description: "RBAC test layer",
            GeometryType: GeometryType.Point,
            SpatialReference: spatialRef,
            Fields: fields,
            Extent: extent,
            DefaultVisibility: true,
            Metadata: metadata);
    }
}

internal sealed class TestCrsRegistry : ICrsRegistry
{
    private static readonly CrsDefinition _crs84 = new("http://www.opengis.net/def/crs/OGC/1.3/CRS84", 4326, AxisOrder.EastNorth, true);
    private static readonly CrsDefinition _epsg4326 = new("http://www.opengis.net/def/crs/EPSG/0/4326", 4326, AxisOrder.NorthEast, true);
    private static readonly CrsDefinition _epsg3857 = new("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false);

    public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84);
        }

        var normalized = crsIdentifier.Trim().Trim('<', '>');
        if (normalized.Equals("CRS84", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(_crs84.Uri, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84);
        }

        if (TryParseEpsg(normalized, out var srid))
        {
            return ValueTask.FromResult<CrsDefinition?>(ResolveBySrid(srid));
        }

        return ValueTask.FromResult<CrsDefinition?>(null);
    }

    public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<CrsDefinition?>(ResolveBySrid(srid));

    public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ResolveBySrid(srid).HasValue);

    private static CrsDefinition? ResolveBySrid(int srid)
    {
        return srid switch
        {
            4326 => _epsg4326,
            3857 => _epsg3857,
            _ => null
        };
    }

    private static bool TryParseEpsg(string identifier, out int srid)
    {
        srid = 0;

        if (identifier.StartsWith("http://www.opengis.net/def/crs/EPSG/0/", StringComparison.OrdinalIgnoreCase))
        {
            var code = identifier["http://www.opengis.net/def/crs/EPSG/0/".Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        if (identifier.StartsWith("urn:ogc:def:crs:EPSG::", StringComparison.OrdinalIgnoreCase))
        {
            var code = identifier["urn:ogc:def:crs:EPSG::".Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        if (identifier.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var code = identifier["EPSG:".Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        return int.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
    }
}

internal sealed class TestCoordinateTransformService : ICoordinateTransformService
{
    public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<(double MinX, double MinY, double MaxX, double MaxY)?>((minX, minY, maxX, maxY));

    public ValueTask<(double X, double Y)?> TransformPointAsync(
        double x,
        double y,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<(double X, double Y)?>((x, y));
}

internal sealed class NoOpGeometryTopologyValidator : IGeometryTopologyValidator
{
    public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryValidationResult.Success());

    public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryRepairResult.NotNeeded(wkb));
}
