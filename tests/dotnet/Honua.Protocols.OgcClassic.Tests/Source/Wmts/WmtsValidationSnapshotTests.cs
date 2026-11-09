// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wmts;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

public sealed class WmtsValidationSnapshotTests
{
    [UnitTest]
    public async Task HandleWmts_ValidationHasNoSnapshot_ReturnsUnavailableWithoutASecondRead()
    {
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("test", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(ResourceValidationResult.Success(new MetadataV2Service
            {
                Metadata = new() { Id = "test", Name = "test" },
                Protocols = [ServiceProtocols.Wmts]
            })));
        var provider = Substitute.For<IMetadataV2GraphProvider>();
        var routes = Substitute.For<IRouteParameterValidator>();
        routes.ValidateServiceId(Arg.Any<HttpContext>()).Returns(ValidationResult<string>.Success("test"));
        using var services = new ServiceCollection().AddLogging().AddSingleton(validator).AddSingleton(provider)
            .AddSingleton(routes).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["serviceId"] = "test";
        context.Request.Path = "/ogc/services/test/wmts";
        context.Request.QueryString = new("?SERVICE=WMTS&REQUEST=GetCapabilities");
        context.Response.Body = new MemoryStream();

        var result = await WmtsRequestHandlers.HandleWmts(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        await provider.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
    }
}
