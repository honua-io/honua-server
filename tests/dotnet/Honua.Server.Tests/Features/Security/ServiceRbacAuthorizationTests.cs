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
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OgcGeoJsonFeature = Honua.Server.Features.Protocols.Ogc.Api.Features.Models.GeoJsonFeature;

namespace Honua.Server.Tests.Features.Security;

public sealed class FeatureServerServiceRbacTests
{
    private const string CalculateExpression = "[{\"field\":\"name\",\"sqlExpression\":\"'RBAC'\"}]";

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/append")]
    public async Task Append_WithAnonymousClient_AndEmptyEdits_ReturnsUnauthorized()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            edits = "[]",
            sourceFormat = "json",
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Append)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/append")]
    public async Task Append_WithReadOnlyRole_AndEmptyEdits_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var payload = JsonSerializer.Serialize(new
        {
            edits = "[]",
            sourceFormat = "json",
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/append",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithAnonymousClient_AndMalformedBody_ReturnsUnauthorizedBeforeBodyValidation()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_WithReadOnlyRole_AndMalformedBody_ReturnsForbiddenBeforeBodyValidation()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/synchronizeReplica",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_WithReadOnlyRole_AndMalformedBody_ReturnsForbiddenBeforeBodyValidation()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "reader");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/unRegisterReplica",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
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
    [Protocol(TestProtocols.FeatureServer)]
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

public sealed class GeoServicesRouteValidationTests
{
    private const string NumericLeadingService = "123service";

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_Metadata_AllowsNumericLeadingServiceId()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceName: NumericLeadingService,
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymous: true)));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/rest/services/{NumericLeadingService}/FeatureServer?f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.MapServer)]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task MapServer_Metadata_AllowsNumericLeadingServiceId()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceName: NumericLeadingService,
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymous: true)));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/rest/services/{NumericLeadingService}/MapServer?f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }
}

public sealed class WmsServiceRbacTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task GetCapabilities_WithAnonymousClient_ReturnsWmsAccessDeniedException()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        body.Should().Contain("ServiceExceptionReport");
        body.Should().Contain("code=\"AccessDenied\"");
    }
}

public sealed class OgcServiceRbacTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
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
    [Protocol(TestProtocols.OgcApiFeatures)]
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
    [Protocol(TestProtocols.OgcApiFeatures)]
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

public sealed class FeatureServerServiceAccessPolicyTests
{
    private const string CalculateExpression = "[{\"field\":\"name\",\"sqlExpression\":\"'RBAC'\"}]";

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithAnonymousClient_AndAnonymousWriteServicePolicy_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymousWrite: true)));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithAnonymousClient_AndAnonymousWriteServicePolicy_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymousWrite: true)));
        using var client = factory.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "anonymous-replica",
            layers = ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithAnonymousClient_AndAnonymousWriteLayerPolicy_AllowsMatchingLayer()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymousWrite = true
                }));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithLayerWriteRolePolicy_AllowsMatchingRole()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(writeRoles: ["alpha-writer"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-writer");

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/applyEdits",
            ServiceRbacTestFixture.CreateApplyEditsContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Calculate)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    public async Task Calculate_WithAnonymousClient_AndAnonymousWriteLayerPolicy_AllowsMatchingLayer()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymousWrite = true
                }));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/{ServiceRbacTestFixture.AlphaLayerId}/calculate?calcExpression={Uri.EscapeDataString(CalculateExpression)}&f=json");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }
}

public sealed class FeatureServerReplicationAccessPolicyTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithAnonymousClient_AndAnonymousWriteLayerPolicy_AllowsMatchingLayer()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymousWrite = true
                }));
        using var client = factory.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "anonymous-create",
            layers = ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    public async Task CreateReplica_WithLayerWriteRolePolicy_AllowsMatchingRole()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(writeRoles: ["alpha-writer"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-writer");

        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "role-create",
            layers = ServiceRbacTestFixture.AlphaLayerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ExtractChanges)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/extractChanges")]
    public async Task ExtractChanges_WithAnonymousClient_AndAnonymousReadLayerPolicy_AllowsMatchingLayer()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymous = true,
                    AllowAnonymousWrite = true
                }));
        using var adminClient = ServiceRbacTestFixture.CreateClient(factory, "admin");
        using var anonymousClient = factory.CreateClient();

        var replicaId = await CreateReplicaAsync(
            adminClient,
            ServiceRbacTestFixture.AlphaService,
            "anonymous-extract",
            ServiceRbacTestFixture.AlphaLayerId);

        var extractPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        var response = await anonymousClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/extractChanges",
            new StringContent(extractPayload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_WithAnonymousClient_AndAnonymousWriteLayerPolicy_AllowsMatchingLayer()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymousWrite = true
                }));
        using var adminClient = ServiceRbacTestFixture.CreateClient(factory, "admin");
        using var anonymousClient = factory.CreateClient();

        var replicaId = await CreateReplicaAsync(
            adminClient,
            ServiceRbacTestFixture.AlphaService,
            "anonymous-sync",
            ServiceRbacTestFixture.AlphaLayerId);

        var syncPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        var response = await anonymousClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/synchronizeReplica",
            new StringContent(syncPayload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    public async Task UnRegisterReplica_WithAnonymousClient_AndAnonymousWriteLayerPolicy_AllowsMatchingLayer()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymousWrite = true
                }));
        using var adminClient = ServiceRbacTestFixture.CreateClient(factory, "admin");
        using var anonymousClient = factory.CreateClient();

        var replicaId = await CreateReplicaAsync(
            adminClient,
            ServiceRbacTestFixture.AlphaService,
            "anonymous-unregister",
            ServiceRbacTestFixture.AlphaLayerId);

        var unregisterPayload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        var response = await anonymousClient.PostAsync(
            $"/rest/services/{ServiceRbacTestFixture.AlphaService}/FeatureServer/unRegisterReplica",
            new StringContent(unregisterPayload, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    private static async Task<string> CreateReplicaAsync(
        HttpClient client,
        string serviceId,
        string replicaName,
        int layerId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName,
            layers = layerId.ToString(CultureInfo.InvariantCulture),
            f = "json"
        });

        var response = await client.PostAsync(
            $"/rest/services/{serviceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var replicaId = document.RootElement.GetProperty("replicaID").GetString();
        replicaId.Should().NotBeNullOrWhiteSpace();
        return replicaId!;
    }
}

public sealed class OgcServiceAccessPolicyTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithAdminRole_RespectsServiceWritePolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                betaServiceMetadata: new AccessPolicy
                {
                    AllowedWriteRoles = ["beta-writer"]
                }));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.BetaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithAnonymousClient_AndAnonymousWriteServicePolicy_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymousWrite: true)));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithLayerWriteRolePolicy_AllowsMatchingRole()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(writeRoles: ["alpha-writer"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-writer");

        var response = await client.PostAsync(
            $"/ogc/features/collections/{ServiceRbacTestFixture.AlphaLayerId}/items",
            ServiceRbacTestFixture.CreateOgcFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiTiles)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task GetCollection_WithAnonymousClient_RespectsServiceReadPolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"]),
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymous = true
                }));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/ogc/tiles/collections/{ServiceRbacTestFixture.AlphaLayerId}");

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }
}

public sealed class ODataServiceRbacTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = ServiceRbacTestFixture.GetPropertyCaseInsensitive(document.RootElement, "responses");
        responses.GetArrayLength().Should().Be(1);
        ServiceRbacTestFixture.GetPropertyCaseInsensitive(responses[0], "status").GetInt32().Should().Be(403);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithAnonymousClient_AllowsPublicLayerReads()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy
                {
                    AllowAnonymous = true
                }));
        using var client = factory.CreateClient();

        var batchRequest = new ODataBatchRequest
        {
            Requests = ImmutableArray.Create(new ODataBatchRequestItem
            {
                Id = "read-alpha",
                Method = "GET",
                Url = $"Layers({ServiceRbacTestFixture.AlphaLayerId})"
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = ServiceRbacTestFixture.GetPropertyCaseInsensitive(responseDocument.RootElement, "responses");
        responses.GetArrayLength().Should().Be(1);
        ServiceRbacTestFixture.GetPropertyCaseInsensitive(responses[0], "status").GetInt32().Should().Be(200);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithAdminRole_RespectsServiceWritePolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                betaServiceMetadata: new AccessPolicy
                {
                    AllowedWriteRoles = ["beta-writer"]
                }));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "admin");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.BetaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithAnonymousClient_AndAnonymousWriteServicePolicy_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymousWrite: true)));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task CreateFeature_WithLayerWriteRolePolicy_AllowsMatchingRole()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(writeRoles: ["alpha-writer"])));
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-writer");

        var response = await client.PostAsync(
            $"/odata/Layers({ServiceRbacTestFixture.AlphaLayerId})/Features",
            ServiceRbacTestFixture.CreateODataFeatureContent());

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithAdminRole_RespectsServiceWritePolicy()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                betaServiceMetadata: new AccessPolicy
                {
                    AllowedWriteRoles = ["beta-writer"]
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

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = ServiceRbacTestFixture.GetPropertyCaseInsensitive(document.RootElement, "responses");
        responses.GetArrayLength().Should().Be(1);
        ServiceRbacTestFixture.GetPropertyCaseInsensitive(responses[0], "status").GetInt32().Should().Be(403);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_WithAnonymousClient_AndAnonymousWriteServicePolicy_AllowsMatchingService()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(allowAnonymousWrite: true)));
        using var client = factory.CreateClient();

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
                        ["name"] = "Anonymous Policy Batch"
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(batchRequest, ODataJsonContext.Default.ODataBatchRequest);
        var response = await client.PostAsync(
            "/odata/$batch",
            new StringContent(json, Encoding.UTF8, "application/json"));

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = ServiceRbacTestFixture.GetPropertyCaseInsensitive(document.RootElement, "responses");
        responses.GetArrayLength().Should().Be(1);
        ServiceRbacTestFixture.GetPropertyCaseInsensitive(responses[0], "status").GetInt32().Should().Be(201);
    }
}

public sealed class ODataServiceBoundaryTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.ODataV4)]
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
    [Protocol(TestProtocols.OgcApiFeatures)]
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
    [Protocol(TestProtocols.OgcApiFeatures)]
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
    [Protocol(TestProtocols.OgcApiFeatures)]
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
