// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Security;

public sealed class DataPlaneAuthorizationHardeningTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServerApplyEdits_SecondaryEditorWithoutCanonicalGrant_IsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(betaAlsoIncludesAlphaLayer: true, reverseServiceOrder: true));
        using var secondaryOnlyClient = ServiceRbacTestFixture.CreateClient(
            factory, $"data-editor:{ServiceRbacTestFixture.BetaService}");

        using var denied = await PostApplyEditsAsync(
            secondaryOnlyClient,
            ServiceRbacTestFixture.BetaService,
            ServiceRbacTestFixture.AlphaLayerId);

        await denied.AssertGeoServicesErrorAsync(403);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServerApplyEdits_SecondaryAndCanonicalEditor_IsAllowed()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(betaAlsoIncludesAlphaLayer: true, reverseServiceOrder: true));
        using var bothServicesClient = ServiceRbacTestFixture.CreateClient(
            factory,
            $"data-editor:{ServiceRbacTestFixture.BetaService}",
            $"data-editor:{ServiceRbacTestFixture.AlphaService}");

        using var allowed = await PostApplyEditsAsync(
            bothServicesClient,
            ServiceRbacTestFixture.BetaService,
            ServiceRbacTestFixture.AlphaLayerId);

        await ServiceRbacTestFixture.AssertStatusAsync(allowed, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task FeatureServerApplyEdits_ReadOnlyAllowedRoles_DeniesViewerAndAllowsEditorWithRole()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(static () =>
            new RbacTestLayerCatalog(
                alphaLayerMetadata: new AccessPolicy { AllowedRoles = ["viewer"] }));
        using var viewerClient = ServiceRbacTestFixture.CreateClient(factory, "viewer");
        using var editorOnlyClient = ServiceRbacTestFixture.CreateClient(factory, $"data-editor:{ServiceRbacTestFixture.AlphaService}");
        using var viewerEditorClient = ServiceRbacTestFixture.CreateClient(factory, "viewer", $"data-editor:{ServiceRbacTestFixture.AlphaService}");
        using var viewerAdminClient = ServiceRbacTestFixture.CreateClient(factory, "viewer", "admin");

        using var viewerWrite = await PostApplyEditsAsync(
            viewerClient, ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId);
        await viewerWrite.AssertGeoServicesErrorAsync(403);

        using var editorWithoutReadRole = await PostApplyEditsAsync(
            editorOnlyClient, ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId);
        await editorWithoutReadRole.AssertGeoServicesErrorAsync(403);

        using var viewerEditorWrite = await PostApplyEditsAsync(
            viewerEditorClient, ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId);
        await ServiceRbacTestFixture.AssertStatusAsync(viewerEditorWrite, HttpStatusCode.OK);

        using var viewerAdminWrite = await PostApplyEditsAsync(
            viewerAdminClient, ServiceRbacTestFixture.AlphaService, ServiceRbacTestFixture.AlphaLayerId);
        await ServiceRbacTestFixture.AssertStatusAsync(viewerAdminWrite, HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> PostApplyEditsAsync(
        HttpClient client,
        string serviceId,
        int layerId)
    {
        using var content = ServiceRbacTestFixture.CreateApplyEditsContent();
        return await client.PostAsync(
            $"/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits",
            content);
    }

    [UnitTest]
    public async Task EvaluateResourceDataEditorAsync_ReadOnlyAllowedRoles_DoesNotGrantWrite()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { DataEditorRoles = ["data-editor"] }));
        await using var provider = services.BuildServiceProvider();

        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-1", Name = "parcels" },
            AccessPolicy = new AccessPolicy { AllowedRoles = ["viewer"], AllowedWriteRoles = null },
        };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-1", Name = "alpha" },
        };

        var request = new ServiceDataEditorAuthorization.ResourceWriteRequest(resource, service, AuthorizationOperation.Update);
        var viewerContext = CreateContext(provider, "viewer");
        var viewerDecision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
            viewerContext, request, CancellationToken.None);
        viewerDecision.IsAllowed.Should().BeFalse();

        var editorOnlyContext = CreateContext(provider, "data-editor");
        var editorOnlyDecision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
            editorOnlyContext, request, CancellationToken.None);
        editorOnlyDecision.IsAllowed.Should().BeFalse();

        var editorContext = CreateContext(provider, "viewer", "data-editor");
        var editorDecision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
            editorContext, request, CancellationToken.None);
        editorDecision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public void AccessPolicyEvaluator_EmptyAllowedWriteRoles_FallsBackToAllowedRolesGate()
    {
        var evaluator = new AccessPolicyEvaluator();
        var policy = new AccessPolicy { AllowedRoles = ["viewer"], AllowedWriteRoles = [] };

        var editorWithoutViewer = evaluator.Evaluate(CreatePrincipal("data-editor"), policy, servicePolicy: null, AccessScope.Write);
        editorWithoutViewer.IsAllowed.Should().BeFalse();

        var viewerWithEditor = evaluator.Evaluate(CreatePrincipal("viewer", "data-editor"), policy, servicePolicy: null, AccessScope.Write);
        viewerWithEditor.IsAllowed.Should().BeTrue();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider, params string[] roles)
        => new()
        {
            RequestServices = provider,
            User = CreatePrincipal(roles),
        };

    private static ClaimsPrincipal CreatePrincipal(params string[] roles)
        => new(new ClaimsIdentity(
            roles.Select(role => new Claim(ClaimTypes.Role, role)).Prepend(new Claim(ClaimTypes.NameIdentifier, "user-1")),
            "Test"));
}
