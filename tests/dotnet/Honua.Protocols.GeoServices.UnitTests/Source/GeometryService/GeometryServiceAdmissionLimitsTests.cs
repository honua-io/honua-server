// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.GeometryService;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using static Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService.GeometryServiceAdmissionTests;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// Waiting for a place, the share of callers that are not signed in, and the route group as a
// whole. In process; the component host is not needed.
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryServiceAdmissionLimitsTests
{
    private const string GeometryServer = GeometryServiceTestContexts.GeometryServer;
    private const GeometryServiceCaller SignedIn = GeometryServiceCaller.SignedIn;
    private const GeometryServiceCaller NotSignedIn = GeometryServiceCaller.NotSignedIn;

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Enter_AtCapacity_TakesAPlaceFreedWhileItWaits()
    {
        var admission = Admission(queueWait: TimeSpan.FromSeconds(30));
        var first = admission.TryEnter(1, SignedIn);
        admission.TryEnter(1, SignedIn).Should().NotBeNull();

        var waiting = admission.EnterAsync(1, SignedIn, CancellationToken.None).AsTask();
        waiting.IsCompleted.Should().BeFalse();
        first!.Dispose();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Enter_AtCapacity_GivesUpWhenNoPlaceFreesInTime()
    {
        var admission = Admission(queueWait: TimeSpan.FromMilliseconds(20));
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().NotBeNull();

        (await admission.EnterAsync(1, SignedIn, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_CallerNotSignedIn_BodyLargerThanItsShare_IsRefusedWithoutRunning()
    {
        var admission = Admission();
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST", admission);
        context.Request.ContentLength = 501;
        context.Response.Body = new MemoryStream();
        var ran = false;

        var result = await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ =>
            {
                ran = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        ran.Should().BeFalse();
        await AssertGeoServicesErrorAsync(context, (IResult)result!, StatusCodes.Status413PayloadTooLarge);
        admission.TryEnter(500, NotSignedIn).Should().NotBeNull("the refused request held nothing");
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData(1_000L)]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_CallerNotSignedIn_BodyIsLimitedToItsShare(long? bodyLimit)
    {
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST", Admission());
        var limit = new BodySizeLimit { MaxRequestBodySize = bodyLimit };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);

        await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(null));

        limit.MaxRequestBodySize.Should().Be(500);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_PreviouslyReadForm_WithinShare_IsAdmitted()
    {
        var ran = await RunPreviouslyReadFormAsync(400);
        ran.Should().BeTrue("the form fields fit within the 500-byte share");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_PreviouslyReadForm_AboveShare_IsRefused()
    {
        var ran = await RunPreviouslyReadFormAsync(600);
        ran.Should().BeFalse("the form fields exceed the 500-byte share");
    }

    private static async Task<bool> RunPreviouslyReadFormAsync(int valueLength)
    {
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST", Admission());
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodySizeLimit { IsReadOnly = true });
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["geometries"] = new string('g', valueLength) })));
        context.Response.Body = new MemoryStream();
        var ran = false;

        await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ =>
            {
                ran = true;
                return ValueTask.FromResult<object?>(null);
            });

        return ran;
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_SignedInCaller_BodyLimitIsUnchanged()
    {
        var context = GeometryServiceTestContexts.Create(SignedIn, "POST", Admission());
        var limit = new BodySizeLimit { MaxRequestBodySize = 1_000 };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);

        await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(null));

        limit.MaxRequestBodySize.Should().Be(1_000);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_CallerNotSignedIn_IsClosedAfterTheHoldLimitAndFreedWhenItEnds()
    {
        var admission = Admission(holdLimit: TimeSpan.FromMilliseconds(50));
        var (lifetime, response) = await RunHeldRequestAsync(admission, NotSignedIn);

        SpinWait.SpinUntil(() => lifetime.Aborted, TimeSpan.FromSeconds(30)).Should().BeTrue();
        using (var signedIn = admission.TryEnter(1, SignedIn))
        {
            signedIn.Should().NotBeNull();
            admission.TryEnter(1, SignedIn).Should().BeNull("the closed request holds its place until it ends");
        }

        await response.CompleteAsync();

        Enumerable.Range(0, 2).Select(_ => admission.TryEnter(1, SignedIn)).ToArray().Should().NotContainNulls();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_CallerNotSignedIn_EndingBeforeTheHoldLimit_IsNotClosed()
    {
        var admission = Admission(holdLimit: TimeSpan.FromMilliseconds(100));
        var (lifetime, response) = await RunHeldRequestAsync(admission, NotSignedIn);

        await response.CompleteAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        lifetime.Aborted.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_CallerNotSignedIn_BodyOfUndeclaredLengthWithoutABodyLimit_IsRefused()
    {
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST", Admission());
        context.Response.Body = new MemoryStream();
        var ran = false;

        var result = await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ =>
            {
                ran = true;
                return ValueTask.FromResult<object?>(null);
            });

        ran.Should().BeFalse();
        await AssertGeoServicesErrorAsync(context, (IResult)result!, StatusCodes.Status413PayloadTooLarge);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_SignedInCaller_IsNotClosedByTheHoldLimit()
    {
        var admission = Admission(holdLimit: TimeSpan.FromMilliseconds(20));
        var (lifetime, _) = await RunHeldRequestAsync(admission, SignedIn);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        lifetime.Aborted.Should().BeFalse();
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull("the signed-in request still holds its place");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task MappedOperationRoute_AtCapacity_AnswersRetryBeforeTheOperation()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddGeometryService();
        builder.Services.AddSingleton(Substitute.For<IGeometryOperationService>());
        builder.Services.AddSingleton(Substitute.For<IGeometryConverter>());
        builder.Services.AddSingleton(Substitute.For<ICrsDetectionService>());
        builder.Services.AddSingleton(Substitute.For<ICrsRegistry>());
        await using var app = builder.Build();
        app.MapGeometryServiceEndpoints();
        var densify = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == GeometryServer + "/densify"
                && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Post));
        var admission = app.Services.GetRequiredService<GeometryServiceAdmission>();
        var held = new List<IDisposable>();
        while (admission.TryEnter(1, SignedIn) is { } place)
        {
            held.Add(place);
        }

        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = GeometryServer + "/densify";
        context.Request.ContentLength = 1;
        context.Response.Body = new MemoryStream();

        await densify.RequestDelegate!(context);

        context.Response.Headers.RetryAfter.ToString().Should().Be("1");
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        held.ForEach(place => place.Dispose());
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void EveryMappedOperationRoute_IsAdmittedAndClassifiedAsAnOperation()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddGeometryService();
        using var app = builder.Build();
        app.MapGeometryServiceEndpoints();
        var operations = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Where(route => route.StartsWith(GeometryServer + "/", StringComparison.Ordinal))
            .Select(route => route[(GeometryServer.Length + 1)..])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        operations.Should().HaveCountGreaterThan(20);
        app.Services.GetService<GeometryServiceAdmission>().Should().NotBeNull();
        operations.Should().AllSatisfy(operation =>
        {
            Classify($"{GeometryServer}/{operation}").Should().Be((operation, true));
            Classify($"{GeometryServer}/{operation.ToUpperInvariant()}").Should().Be((operation, true));
        });
        Classify(GeometryServer).Should().Be(("metadata", false));
        Classify(GeometryServer + "/").Should().Be(("metadata", false));
        Classify(GeometryServer + "/unknownOperation").Should().Be(("metadata", false));
    }

    private static (string? Operation, bool IsGeometryOperation) Classify(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return (RequestTelemetryClassifier.ResolveOperation(context), RequestTelemetryClassifier.IsGeometryServiceOperation(context));
    }

    private static async Task<(RecordingLifetime Lifetime, CompletingResponseFeature Response)> RunHeldRequestAsync(
        GeometryServiceAdmission admission, GeometryServiceCaller caller)
    {
        var context = GeometryServiceTestContexts.Create(caller, "POST", admission);
        context.Request.ContentLength = 1;
        var lifetime = new RecordingLifetime();
        var response = new CompletingResponseFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Features.Set<IHttpResponseFeature>(response);

        await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(null));

        return (lifetime, response);
    }

    private static GeometryServiceAdmission Admission(TimeSpan queueWait = default, TimeSpan? holdLimit = null)
        => new(new GeometryServiceAdmission.Bound(2, 1_000), 1_000, new GeometryServiceAdmission.Timing(queueWait, holdLimit, TimeProvider.System));

    private sealed class BodySizeLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        private int _aborted;

        public bool Aborted => Volatile.Read(ref _aborted) == 1;

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Interlocked.Exchange(ref _aborted, 1);
    }
}
