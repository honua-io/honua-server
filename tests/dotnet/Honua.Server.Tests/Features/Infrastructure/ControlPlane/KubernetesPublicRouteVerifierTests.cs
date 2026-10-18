// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class KubernetesPublicRouteVerifierTests
{
    [UnitTheory]
    [InlineData("valid", true)]
    [InlineData("wrong-lb", false)]
    [InlineData("wrong-zone", false)]
    [InlineData("different-public-name", false)]
    [InlineData("weighted-alternative", false)]
    [InlineData("different-alternative", false)]
    [InlineData("multivalue", false)]
    [InlineData("private-zone", false)]
    [InlineData("wrong-account", false)]
    [InlineData("wrong-listener", false)]
    [InlineData("undelegated-zone", false)]
    public async Task PublicDomainRequiresExactObservedLoadBalancerAndAllServingRecords(string scenario, bool expected)
    {
        const string arn = "arn:aws:elasticloadbalancing:us-west-2:123456789012:loadbalancer/net/honua/123";
        const string lbHost = "honua-123.elb.us-west-2.amazonaws.com";
        var dns = Substitute.For<IAmazonRoute53>();
        var identity = Substitute.For<IAmazonSecurityTokenService>();
        var client = Substitute.For<IAmazonElasticLoadBalancingV2>();
        identity.GetCallerIdentityAsync(Arg.Any<GetCallerIdentityRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetCallerIdentityResponse
            { Account = scenario == "wrong-account" ? "999999999999" : "123456789012", Arn = "arn:aws:sts::123456789012:assumed-role/honua/session" }));
        client.DescribeLoadBalancersAsync(Arg.Any<DescribeLoadBalancersRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DescribeLoadBalancersResponse { LoadBalancers =
            [new() { LoadBalancerArn = arn, DNSName = scenario == "wrong-lb" ? "other.elb.amazonaws.com" : lbHost,
                CanonicalHostedZoneId = "ZELB", State = new() { Code = "active" }, Scheme = "internet-facing" }] }));
        client.DescribeListenersAsync(Arg.Any<DescribeListenersRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DescribeListenersResponse
            { Listeners = [new() { LoadBalancerArn = arn, Port = scenario == "wrong-listener" ? 8443 : 443, Protocol = "TLS" }] }));
        dns.GetHostedZoneAsync(Arg.Any<GetHostedZoneRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetHostedZoneResponse
            { HostedZone = new() { Id = "/hostedzone/ZPUBLIC", Name = "example.test.", Config = new() { PrivateZone = scenario == "private-zone" } },
                DelegationSet = new() { NameServers = ["ns1.example.net", "ns2.example.net"] } }));
        List<ResourceRecordSet> records = [new()
        {
            Name = scenario == "different-public-name" ? "dualstack.maps.example.test." : "maps.example.test.", Type = "A", MultiValueAnswer = scenario == "multivalue",
            AliasTarget = new() { DNSName = "dualstack." + lbHost + ".", HostedZoneId = scenario == "wrong-zone" ? "ZOTHER" : "ZELB" }
        }];
        if (scenario is "weighted-alternative" or "different-alternative")
            records.Add(new() { Name = "maps.example.test.", Type = "AAAA", SetIdentifier = scenario == "weighted-alternative" ? "other-region" : null,
                AliasTarget = new() { DNSName = scenario == "different-alternative" ? "another.elb.amazonaws.com" : lbHost, HostedZoneId = "ZELB" } });
        dns.ListResourceRecordSetsAsync(Arg.Any<ListResourceRecordSetsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListResourceRecordSetsResponse { ResourceRecordSets = records, IsTruncated = false }));
        using var verifier = new AwsKubernetesPublicRouteVerifier(() => dns, () => identity, region =>
        { region.Should().Be("us-west-2"); return client; }, (zone, host, nameServers, _) =>
        {
            zone.Should().Be("example.test."); host.Should().Be("maps.example.test");
            nameServers.Should().BeEquivalentTo("ns1.example.net", "ns2.example.net");
            return Task.FromResult(scenario != "undelegated-zone");
        });
        var parameters = new Dictionary<string, string>
        {
            ["kubernetes.cluster_arn"] = "arn:aws:eks:us-west-2:123456789012:cluster/honua",
            ["kubernetes.load_balancer_arn"] = arn, ["kubernetes.dns_zone_id"] = "ZPUBLIC",
            ["kubernetes.dns_record_name"] = "maps.example.test", ["kubernetes.public_url"] = "https://maps.example.test/honua"
        };
        (await verifier.VerifyAsync(parameters, lbHost)).Should().Be(expected);
    }

    [UnitTest]
    public void ConstructionAndDisposalDoNotResolveCloudCredentialsOrClients()
    {
        using var verifier = new AwsKubernetesPublicRouteVerifier(
            () => throw new InvalidOperationException("no DNS on startup"),
            () => throw new InvalidOperationException("no identity on startup"),
            _ => throw new InvalidOperationException("no LB on startup"),
            (_, _, _, _) => throw new InvalidOperationException("no public DNS on startup"));
    }
    [UnitTheory]
    [InlineData("valid", true)]
    [InlineData("no-resolver", false)]
    [InlineData("undelegated", false)]
    [InlineData("wrong-owner", false)]
    [InlineData("child-zone", false)]
    [InlineData("outside-zone", false)]
    public async Task DelegationRequiresExactZoneOwnerCompleteNsSetAndHostnameAuthority(string scenario, bool expected)
    {
        var options = new KubernetesPublicDnsOptions { Resolvers = scenario == "no-resolver" ? [] : ["192.0.2.53"] };
        var reader = new KubernetesPublicDnsDelegation(options, (resolver, zone, host, _) =>
        {
            resolver.ToString().Should().Be("192.0.2.53"); zone.Should().Be("example.test");
            IReadOnlyList<(string Owner, string NameServer)> records = scenario == "undelegated"
                ? [("example.test", "ns.other.test")]
                : [(scenario == "wrong-owner" ? "other.test" : "example.test", "ns1.example.net"), ("example.test", "ns2.example.net")];
            return Task.FromResult(new PublicDnsDelegationProof(records, scenario == "child-zone" ? "maps.example.test" : "example.test"));
        });
        (await reader.VerifyAsync("example.test.", scenario == "outside-zone" ? "maps.notexample.test" : "maps.example.test",
            ["ns1.example.net.", "ns2.example.net."], CancellationToken.None)).Should().Be(expected);
    }
}
