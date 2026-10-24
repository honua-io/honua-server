// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class DegradedStartupMutationFenceTests
{
    [UnitTheory]
    [InlineData(null, ReadinessReasonCodes.DatabaseStartupDegraded)]
    [InlineData(ReadinessReasonCodes.SchemaFloor, ReadinessReasonCodes.SchemaFloor)]
    public async Task ApplyEdits_WhileAwaitingDatabase_Returns503AndDoesNotRun(string? reasonCode, string expectedReason)
    {
        var executed = false;
        var context = Context(
            HttpMethods.Post,
            "/rest/services/parcels/FeatureServer/0/applyEdits",
            Awaiting(reasonCode));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[ReadinessReasonCodes.HeaderName].ToString().Should().Be(expectedReason);
    }

    [UnitTheory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task MutatingMethod_WhileAwaitingDatabase_Returns503AndDoesNotRun(string method)
    {
        var executed = false;
        var context = Context(method, "/rest/services/parcels/FeatureServer/0/applyEdits", Awaiting(null));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[ReadinessReasonCodes.HeaderName].ToString()
            .Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);
    }

    [UnitTheory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Read_WhileAwaitingDatabase_IsNotRefused(string method)
    {
        var executed = false;
        var context = Context(method, "/rest/services/parcels/FeatureServer/0/query", Awaiting(ReadinessReasonCodes.SchemaFloor));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Headers.ContainsKey(ReadinessReasonCodes.HeaderName).Should().BeFalse();
    }

    [UnitTest]
    public async Task HealthzPost_WhileAwaitingDatabase_IsNotRefused()
    {
        var executed = false;
        var context = Context(HttpMethods.Post, "/healthz/ready", Awaiting(ReadinessReasonCodes.SchemaFloor));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [UnitTest]
    public async Task ApplyEdits_WhenDatabaseIsReady_IsNotRefused()
    {
        var executed = false;
        var context = Context(
            HttpMethods.Post,
            "/rest/services/parcels/FeatureServer/0/applyEdits",
            new DegradedStartupContext());

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Headers.ContainsKey(ReadinessReasonCodes.HeaderName).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("/rest/services/GeocodeServer/findAddressCandidates")]
    [InlineData("/rest/services/GeocodeServer/reverseGeocode")]
    [InlineData("/rest/services/GeocodeServer/suggest")]
    [InlineData("/rest/services/locator/GeocodeServer")]
    [InlineData("/rest/services/GeocodeServer/geocodeAddresses")]
    [InlineData("/rest/services/GEOCODESERVER/findAddressCandidates")]
    public async Task GeocodeReadPost_WhileAwaitingDatabase_IsNotRefused(string path)
    {
        var executed = false;
        var context = Context(HttpMethods.Post, path, Awaiting(null), body: "address=1%20Main", contentType: "application/x-www-form-urlencoded");
        string? seen = null;

        await InvokeAsync(context, () =>
        {
            executed = true;
            seen = ReadBody(context);
        });

        executed.Should().BeTrue();
        seen.Should().Be("address=1%20Main");
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Headers.ContainsKey(ReadinessReasonCodes.HeaderName).Should().BeFalse();
    }

    [UnitTest]
    public async Task GeocodePut_WhileAwaitingDatabase_IsRefused()
    {
        var executed = false;
        var context = Context(HttpMethods.Put, "/rest/services/GeocodeServer/findAddressCandidates", Awaiting(null));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [UnitTest]
    public async Task PostPathContainingGeocodeLetters_WhileAwaitingDatabase_IsRefused()
    {
        var executed = false;
        var context = Context(HttpMethods.Post, "/rest/services/NotGeocodeServer/applyEdits", Awaiting(null));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [UnitTheory]
    [InlineData("?request=GetFeature")]
    [InlineData("?SERVICE=WFS&REQUEST=GetFeature")]
    [InlineData("?request=GetCapabilities")]
    [InlineData("?request=DescribeFeatureType")]
    [InlineData("?request=GetPropertyValue")]
    [InlineData("?request=ListStoredQueries")]
    [InlineData("?request=DescribeStoredQueries")]
    public async Task WfsReadQueryPost_WhileAwaitingDatabase_IsNotRefused(string query)
    {
        var executed = false;
        var context = Context(HttpMethods.Post, "/wfs", Awaiting(null), query: query);

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [UnitTest]
    public async Task WfsGetFeatureXmlPost_WhileAwaitingDatabase_IsNotRefusedAndRewindsBody()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- read -->
            <wfs:GetFeature service="WFS" version="2.0.0"/>
            """;
        var executed = false;
        var context = Context(HttpMethods.Post, "/WFS", Awaiting(null), body: xml, contentType: "text/xml");
        string? seen = null;

        await InvokeAsync(context, () =>
        {
            executed = true;
            seen = ReadBody(context);
        });

        executed.Should().BeTrue();
        seen.Should().Contain("wfs:GetFeature");
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [UnitTest]
    public async Task WfsGetFeatureFormPost_WhileAwaitingDatabase_IsNotRefused()
    {
        var executed = false;
        var context = Context(
            HttpMethods.Post,
            "/wfs",
            Awaiting(null),
            body: "service=WFS&request=Get%46eature&version=2.0.0",
            contentType: "application/x-www-form-urlencoded");

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [UnitTheory]
    [InlineData("?request=Transaction", null, null)]
    [InlineData("?request=GetFeatureWithLock", null, null)]
    [InlineData("?request=LockFeature", null, null)]
    [InlineData("?request=CreateStoredQuery", null, null)]
    [InlineData("?request=DropStoredQuery", null, null)]
    [InlineData("?request=NotAnOperation", null, null)]
    [InlineData(null, "<wfs:Transaction service=\"WFS\"/>", "text/xml")]
    [InlineData("?request=GetFeature", "<wfs:Transaction service=\"WFS\"/>", "application/xml")]
    [InlineData("?request=Transaction", "<wfs:GetFeature service=\"WFS\"/>", "text/xml")]
    [InlineData(null, "hello", "text/plain")]
    [InlineData(null, null, null)]
    public async Task WfsMutatingOrUnknownPost_WhileAwaitingDatabase_IsRefused(string? query, string? body, string? contentType)
    {
        var executed = false;
        var context = Context(HttpMethods.Post, "/wfs", Awaiting(null), query, body, contentType);

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[ReadinessReasonCodes.HeaderName].ToString()
            .Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);
    }

    [UnitTest]
    public async Task WfsTransaction_WhenDatabaseIsReady_IsNotRefused()
    {
        var executed = false;
        var context = Context(
            HttpMethods.Post,
            "/wfs",
            new DegradedStartupContext(),
            query: "?request=Transaction");

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    private static DegradedStartupContext Awaiting(string? reasonCode)
    {
        var degraded = new DegradedStartupContext();
        degraded.MarkDegraded(
            migrationsPending: false,
            migrationsAssembly: null,
            reason: "schema behind this build",
            reasonCode: reasonCode);
        return degraded;
    }

    private static DefaultHttpContext Context(
        string method,
        string path,
        DegradedStartupContext degraded,
        string? query = null,
        string? body = null,
        string? contentType = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(degraded).BuildServiceProvider()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query.StartsWith('?') ? query : "?" + query);
        }

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentType = contentType;
            context.Request.ContentLength = bytes.Length;
        }

        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static Task InvokeAsync(HttpContext context, Action onNext)
    {
        var middleware = new DegradedStartupMutationFenceMiddleware(ctx =>
        {
            onNext();
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        return middleware.InvokeAsync(context);
    }
}
