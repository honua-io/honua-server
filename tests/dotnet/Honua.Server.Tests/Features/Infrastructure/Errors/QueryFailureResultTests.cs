// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

[Collection("Unit")]
public sealed class QueryFailureResultTests
{
    private const string LiteralValue = "hidden-literal-7f3a";

    [UnitTheory]
    [InlineData("/ogc/features/collections/c1/items")]
    [InlineData("/ogc/features/collections/c1/items/7")]
    [InlineData("/static/svc/bbox/-1,-1,1,1/64x64.png")]
    public async Task HiddenFieldRejection_IsBadRequest_WithoutEchoingTheQuery(string path)
    {
        var context = CreateContext(path);

        await StandardErrorHelpers.CreateQueryFailure(context, CaptureHiddenFieldRejection(), "failed").ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        ReadBody(context).Should().NotContain(LiteralValue).And.NotContain("secret");
    }

    [UnitTest]
    public async Task Backpressure_KeepsServiceUnavailable()
    {
        var context = CreateContext("/ogc/features/collections/c1/items");

        await StandardErrorHelpers.CreateQueryFailure(context, new ServiceUnavailableException("busy"), "failed").ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [UnitTheory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    public async Task UnexpectedFailure_StaysGenericServerError(Type exceptionType)
    {
        var context = CreateContext("/ogc/features/collections/c1/items");
        var exception = (Exception)Activator.CreateInstance(exceptionType, "internal detail")!;

        await StandardErrorHelpers.CreateQueryFailure(context, exception, "An error occurred while retrieving items.").ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ReadBody(context).Should().Contain("An error occurred while retrieving items.").And.NotContain("internal detail");
    }

    private static MaskedFieldReferenceException CaptureHiddenFieldRejection()
    {
        var query = new FeatureQuery
        {
            Where = $"secret = '{LiteralValue}'",
            EnforcedMaskedFields = ImmutableArray.Create("secret")
        };

        var act = () => FeatureQuerySecurity.Validate(query);

        return act.Should().Throw<MaskedFieldReferenceException>().Which;
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }
}
