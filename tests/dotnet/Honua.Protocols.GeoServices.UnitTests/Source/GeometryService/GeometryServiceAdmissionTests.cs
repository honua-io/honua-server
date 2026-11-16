// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// How many GeometryServer requests the operations work on at once. In process; the component
// host is not needed.
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryServiceAdmissionTests
{
    private const string GeometryServer = GeometryServiceTestContexts.GeometryServer;
    private const GeometryServiceCaller SignedIn = GeometryServiceCaller.SignedIn;
    private const GeometryServiceCaller NotSignedIn = GeometryServiceCaller.NotSignedIn;

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void AtItsOperationLimit_RefusesUntilAnOperationFinishes()
    {
        var admission = Admission(operations: 4, requestBytes: 1_000);
        var places = Enumerable.Range(0, 4).Select(_ => admission.TryEnter(1, SignedIn)).ToArray();

        places.Should().NotContainNulls();
        admission.TryEnter(1, SignedIn).Should().BeNull();

        places[0]!.Dispose();

        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void CallersNotSignedIn_ShareHalfOfTheOperations()
    {
        var admission = Admission(operations: 4, requestBytes: 1_000);

        admission.TryEnter(1, NotSignedIn).Should().NotBeNull();
        var second = admission.TryEnter(1, NotSignedIn);
        second.Should().NotBeNull();
        admission.TryEnter(1, NotSignedIn).Should().BeNull();

        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull();

        second!.Dispose();

        admission.TryEnter(1, NotSignedIn).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void SignedInCallers_MayUseEveryOperation()
    {
        var admission = Admission(operations: 4, requestBytes: 1_000);

        Enumerable.Range(0, 4).Select(_ => admission.TryEnter(1, SignedIn)).ToArray().Should().NotContainNulls();
        admission.TryEnter(1, NotSignedIn).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void RequestBytes_BoundWhatRunsTogether()
    {
        var admission = Admission(operations: 8, requestBytes: 1_000);

        var first = admission.TryEnter(700, SignedIn);
        first.Should().NotBeNull();
        admission.TryEnter(301, SignedIn).Should().BeNull();
        admission.TryEnter(300, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull();

        first!.Dispose();

        admission.TryEnter(700, SignedIn).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void RequestLargerThanTheByteBound_FromASignedInCaller_RunsOnlyAlone()
    {
        var admission = Admission(operations: 8, requestBytes: 1_000);

        var large = admission.TryEnter(5_000, SignedIn);
        large.Should().NotBeNull();
        admission.TryEnter(1, NotSignedIn).Should().BeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull();

        large!.Dispose();
        using var small = admission.TryEnter(1, SignedIn);

        small.Should().NotBeNull();
        admission.TryEnter(5_000, SignedIn).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void RequestFromACallerNotSignedIn_ReservesAtMostHalfTheBytes()
    {
        var admission = Admission(operations: 8, requestBytes: 1_000);

        var large = admission.TryEnter(5_000, NotSignedIn);
        large.Should().NotBeNull();
        admission.TryEnter(1, NotSignedIn).Should().BeNull("callers not signed in hold their whole half");
        admission.TryEnter(500, SignedIn).Should().NotBeNull("the signed-in half stays free");
        admission.TryEnter(1, SignedIn).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void RequestFromACallerNotSignedIn_NeverRunsAloneOverTheBound()
    {
        var admission = Admission(operations: 8, requestBytes: 1_000);
        using var signedIn = admission.TryEnter(900, SignedIn);

        admission.TryEnter(101, NotSignedIn).Should().BeNull();
        admission.TryEnter(100, NotSignedIn).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void CallersNotSignedIn_ShareHalfOfTheBytes()
    {
        var admission = Admission(operations: 8, requestBytes: 1_000);

        admission.TryEnter(400, NotSignedIn).Should().NotBeNull();
        admission.TryEnter(101, NotSignedIn).Should().BeNull();
        admission.TryEnter(100, NotSignedIn).Should().NotBeNull();
        admission.TryEnter(500, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void PlaceReleasedTwice_FreesOneOperation()
    {
        var admission = Admission(operations: 2, requestBytes: 1_000);
        var first = admission.TryEnter(1, SignedIn);
        admission.TryEnter(1, SignedIn).Should().NotBeNull();

        first!.Dispose();
        first.Dispose();

        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task ConcurrentCallers_AreAdmittedExactlyUpToTheOperationLimit()
    {
        const int limit = 4;
        const int callers = 16;
        var admission = Admission(limit, long.MaxValue / 4);
        using var attempted = new CountdownEvent(callers);
        using var release = new ManualResetEventSlim();
        var admitted = 0;

        var running = Enumerable.Range(0, callers).Select(_ => Task.Run(() =>
        {
            var place = admission.TryEnter(1, SignedIn);
            if (place is not null)
            {
                Interlocked.Increment(ref admitted);
            }

            attempted.Signal();
            if (place is not null)
            {
                release.Wait();
                place.Dispose();
            }
        })).ToArray();

        attempted.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        Volatile.Read(ref admitted).Should().Be(limit, "every admitted caller holds its place until released");
        release.Set();
        await Task.WhenAll(running);
        Enumerable.Range(0, limit).Select(_ => admission.TryEnter(1, SignedIn)).ToArray().Should().NotContainNulls();
    }

    [UnitTheory]
    [InlineData(1, 8)]
    [InlineData(2, 8)]
    [InlineData(8, 16)]
    [InlineData(96, 32)]
    [Operation(Operations.Infrastructure)]
    public void DefaultOperationLimit_FollowsTheProcessorCountWithinBounds(int processors, int expected)
    {
        GeometryServiceAdmission.DefaultMaxOperations(processors).Should().Be(expected);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void DefaultByteBound_FitsTheLargestAcceptedRequest()
    {
        var largest = new LimitsOptions().Edits.MaxPayloadSize;

        GeometryServiceAdmission.DefaultMaxRequestBytes(largest).Should().BeInRange(largest, (largest * 2) - 1);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void BodyOfUndeclaredLength_CountsAsTheLargestAcceptedRequest()
    {
        var admission = Admission(operations: 8, requestBytes: 1_000, undeclaredBodyBytes: 900);
        var chunked = GeometryServiceTestContexts.Create(SignedIn, "POST");
        var declared = GeometryServiceTestContexts.Create(SignedIn, "POST");
        declared.Request.ContentLength = 101;

        admission.TryEnter(chunked).Should().NotBeNull();
        admission.TryEnter(declared).Should().BeNull();
        declared.Request.ContentLength = 100;
        admission.TryEnter(declared).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void GetRequest_CountsItsQueryString()
    {
        var admission = Admission(operations: 8, requestBytes: 100);
        var first = GeometryServiceTestContexts.Create(SignedIn);
        first.Request.QueryString = new QueryString("?" + new string('g', 59));
        var second = GeometryServiceTestContexts.Create(SignedIn);
        second.Request.QueryString = new QueryString("?" + new string('g', 40));

        admission.TryEnter(first).Should().NotBeNull();
        admission.TryEnter(second).Should().BeNull();
        second.Request.QueryString = new QueryString("?" + new string('g', 39));
        admission.TryEnter(second).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_AtCapacity_AnswersRetryWithoutRunningTheOperation()
    {
        var admission = Admission(operations: 2, requestBytes: 1_000);
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST", admission);
        context.Request.ContentLength = 1;
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
        result.Should().BeSameAs(Results.Empty);
        context.Response.Headers.RetryAfter.ToString().Should().Be("1");
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Body.Length.Should().BeGreaterThan(0);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_AdmittedRequest_HoldsItsPlaceUntilTheResponseCompletes()
    {
        var admission = Admission(operations: 2, requestBytes: 1_000);
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        var context = GeometryServiceTestContexts.Create(SignedIn, "POST", admission);
        context.Request.ContentLength = 1;
        var response = new CompletingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(response);
        var marker = new object();

        var result = await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ =>
            {
                admission.TryEnter(1, SignedIn).Should().BeNull("the running operation holds the last place");
                return ValueTask.FromResult<object?>(marker);
            });

        result.Should().BeSameAs(marker);
        admission.TryEnter(1, SignedIn).Should().BeNull("the result is still to be written");

        await response.CompleteAsync();

        admission.TryEnter(1, SignedIn).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_OperationThatThrows_FreesItsPlaceWhenTheResponseCompletes()
    {
        var admission = Admission(operations: 2, requestBytes: 1_000);
        var context = GeometryServiceTestContexts.Create(SignedIn, "POST", admission);
        var response = new CompletingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(response);

        var act = async () => await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => throw new InvalidOperationException("operation failed"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await response.CompleteAsync();

        Enumerable.Range(0, 2).Select(_ => admission.TryEnter(1, SignedIn)).ToArray().Should().NotContainNulls();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_ServiceDescription_IsNeverRefused()
    {
        var admission = Admission(operations: 2, requestBytes: 1_000);
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        admission.TryEnter(1, SignedIn).Should().NotBeNull();
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST", admission);
        context.Request.Path = GeometryServer;
        var marker = new object();

        var result = await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(marker));

        result.Should().BeSameAs(marker);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Filter_WithoutAdmissionRegistered_RunsTheOperation()
    {
        var context = GeometryServiceTestContexts.Create(NotSignedIn, "POST");
        var marker = new object();

        var result = await GeometryServiceAdmission.FilterAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ => ValueTask.FromResult<object?>(marker));

        result.Should().BeSameAs(marker);
    }

    private static GeometryServiceAdmission Admission(int operations, long requestBytes, long undeclaredBodyBytes = 1_000)
        => new(new GeometryServiceAdmission.Bound(operations, requestBytes), undeclaredBodyBytes);

    // DefaultHttpContext never completes a response, so nothing registered for disposal runs.
    internal sealed class CompletingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));

        public async Task CompleteAsync()
        {
            for (var index = _completed.Count - 1; index >= 0; index--)
            {
                await _completed[index].Callback(_completed[index].State);
            }

            _completed.Clear();
        }
    }
}
