// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class CapabilityRedemptionTests
{
    private const string Code = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Capability = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly RedemptionTarget Target = new("https://api.honua.test/", "job-1", "2");

    [Fact]
    [Trait("Tier", "Fast")]
    public void Redeem_PostsCodeHeaderToAttemptRouteAndReturnsCapability()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK);

        CapabilityRedemption.Redeem(Target, Code, handler).Should().Be(Capability);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://api.honua.test/api/geoprocessing/jobs/job-1/attempts/2/capability");
        request.Headers.GetValues(CapabilityRedemption.CodeHeader).Should().Equal(Code);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void Redeem_NotYetDispatched_RetriesUntilRedeemed()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Conflict, HttpStatusCode.OK);

        CapabilityRedemption.Redeem(Target, Code, handler).Should().Be(Capability);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Gone)]
    public void Redeem_SpentOrExpiredCode_FailsWithoutRetry(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(status);

        var redeem = () => CapabilityRedemption.Redeem(Target, Code, handler);

        redeem.Should().Throw<InvalidOperationException>().WithMessage($"*HTTP {(int)status}*");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("plain-http")]
    [InlineData("path-job-id")]
    [InlineData("zero-attempt")]
    [InlineData("upper-case-code")]
    public void Redeem_UnsafeInputs_RefusedBeforeAnyRequest(string unsafeInput)
    {
        var (target, code) = unsafeInput switch
        {
            "plain-http" => (new RedemptionTarget("http://api.honua.test", "job-1", "1"), Code),
            "path-job-id" => (new RedemptionTarget("https://api.honua.test", "../job", "1"), Code),
            "zero-attempt" => (new RedemptionTarget("https://api.honua.test", "job-1", "0"), Code),
            _ => (Target, Code.ToUpperInvariant()),
        };
        var handler = new ScriptedHandler();

        var redeem = () => CapabilityRedemption.Redeem(target, code, handler);

        redeem.Should().Throw<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var status = _statuses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? Capability : string.Empty) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
