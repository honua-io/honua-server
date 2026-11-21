// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class AdminApiRouteBindingTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [UnitTest]
    public void AdminApiCatalog_AllDefinitions_AreBoundToMatchingHttpEndpoints()
    {
        using var _ = _factory.CreateClient();
        var endpoints = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .ToArray();

        var violations = AdminHttpOperationRouteBinding.Validate(endpoints, AdminApiOperationCatalog.Definitions);
        violations.Should().BeEmpty(
            "every AdminApiOperationCatalog definition must bind its live HTTP route, method, and OpenAPI operation name");
    }

    [UnitTest]
    public void AdminConnectImportCatalog_AllDefinitions_AreBoundToMatchingHttpEndpoints()
    {
        using var _ = _factory.CreateClient();
        var endpoints = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .ToArray();

        var violations = AdminHttpOperationRouteBinding.Validate(endpoints, AdminConnectImportOperationCatalog.Definitions);
        violations.Should().BeEmpty(
            "every AdminConnectImportOperationCatalog definition must bind its live HTTP route, method, and OpenAPI operation name");
    }

    [UnitTest]
    public void Validate_MissingBinding_ReportsMissingEndpoint()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "admin.services.list");

        var violations = AdminHttpOperationRouteBinding.Validate([], [definition]);

        violations.Should().ContainSingle(item => item.OperationId == definition.OperationId && item.Kind == "MissingEndpoint");
    }

    [UnitTest]
    public void Validate_DuplicateBinding_ReportsDuplicateEndpoint()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "admin.services.list");
        var first = CreateRouteEndpoint(definition, new("/api/v{version:apiVersion}/admin/services", HttpMethods.Get, definition.OpenApiOperationId));
        var second = CreateRouteEndpoint(definition, new("/api/v{version:apiVersion}/admin/services", HttpMethods.Get, definition.OpenApiOperationId));

        var violations = AdminHttpOperationRouteBinding.Validate([first, second], [definition]);

        violations.Should().ContainSingle(item => item.OperationId == definition.OperationId && item.Kind == "DuplicateEndpoint");
    }

    [UnitTest]
    public void Validate_MethodDrift_ReportsMethodMismatch()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "admin.services.protocols.set");
        var endpoint = CreateRouteEndpoint(definition, new("/api/v{version:apiVersion}/admin/services/{serviceName}/protocols", HttpMethods.Post, definition.OpenApiOperationId));

        var violations = AdminHttpOperationRouteBinding.Validate([endpoint], [definition]);

        violations.Should().ContainSingle(item => item.OperationId == definition.OperationId && item.Kind == "MethodMismatch");
    }

    [UnitTest]
    public void Validate_PathDrift_ReportsPathMismatch()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "admin.services.settings.get");
        var endpoint = CreateRouteEndpoint(definition, new("/api/v{version:apiVersion}/admin/services/{serviceName}/config", HttpMethods.Get, definition.OpenApiOperationId));

        var violations = AdminHttpOperationRouteBinding.Validate([endpoint], [definition]);

        violations.Should().ContainSingle(item => item.OperationId == definition.OperationId && item.Kind == "PathMismatch");
    }

    [UnitTest]
    public void Validate_EndpointNameDrift_ReportsEndpointNameMismatch()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == "admin.layer.display.get");
        var endpoint = CreateRouteEndpoint(definition, new("/api/v{version:apiVersion}/admin/metadata/layers/{storageLayerId:int}/display", HttpMethods.Get, "GetAdminLayerDisplay"));

        var violations = AdminHttpOperationRouteBinding.Validate([endpoint], [definition]);

        violations.Should().ContainSingle(item => item.OperationId == definition.OperationId && item.Kind == "EndpointNameMismatch");
    }

    [UnitTest]
    public void ForApi_DriftedRelativeTemplate_ThrowsAtRegistration()
    {
        var act = () => AdminHttpOperationRouteBinding.ForApi("admin.services.settings.get", "/services", "/{serviceName}/wrong");

        act.Should().Throw<InvalidOperationException>().WithMessage("*admin.services.settings.get*");
    }

    [UnitTest]
    public void ForConnectImport_DriftedRelativeTemplate_ThrowsAtRegistration()
    {
        var act = () => AdminHttpOperationRouteBinding.ForConnectImport("admin.connections.test", "/connections", "/{id:guid}/wrong");

        act.Should().Throw<InvalidOperationException>().WithMessage("*admin.connections.test*");
    }

    private readonly record struct TestEndpointSpec(string Pattern, string Method, string EndpointName);

    private static RouteEndpoint CreateRouteEndpoint(IAdminHttpOperationDefinition definition, TestEndpointSpec spec) =>
        new(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse(spec.Pattern),
            0,
            new EndpointMetadataCollection(
                new HttpMethodMetadata([spec.Method]),
                new EndpointNameMetadata(spec.EndpointName),
                definition),
            spec.EndpointName);
}
