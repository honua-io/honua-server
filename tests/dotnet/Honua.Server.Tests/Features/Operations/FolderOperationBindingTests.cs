// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderOperationBindingTests
{
    [UnitTheory]
    [InlineData("folder.list")]
    [InlineData("folder.create")]
    [InlineData("folder.update")]
    [InlineData("folder.delete")]
    [InlineData("service.move")]
    public async Task Route_UsesCanonicalOperationDefinition(string operationId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddApiVersioning();
        await using var app = builder.Build();
        app.MapFolderEndpoints();
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == operationId);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Should().ContainSingle(item =>
                item.RoutePattern.RawText == "/api/v{version:apiVersion}/admin" + definition.Path &&
                item.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(definition.Method.Method)).Subject;

        endpoint.Metadata.GetMetadata<AdminApiOperationCatalog.Definition>().Should().BeSameAs(definition,
            "the executing HTTP route must carry the definition used by MCP and approval mapping");
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName.Should().Be(definition.OpenApiOperationId);
    }
}
