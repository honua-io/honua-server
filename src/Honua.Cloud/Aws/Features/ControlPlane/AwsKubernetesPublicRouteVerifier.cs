// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Amazon;
using Amazon.ElasticLoadBalancingV2;
using Amazon.ElasticLoadBalancingV2.Model;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>Read-only AWS proof of the bound Kubernetes ingress and public Route53 alias.</summary>
internal sealed class AwsKubernetesPublicRouteVerifier : IKubernetesPublicRouteVerifier, IDisposable
{
    private readonly Lazy<IAmazonRoute53> _dns;
    private readonly Lazy<IAmazonSecurityTokenService> _identity;
    private readonly Func<string, IAmazonElasticLoadBalancingV2> _createLoadBalancer;
    private readonly ConcurrentDictionary<string, IAmazonElasticLoadBalancingV2> _loadBalancers = new(StringComparer.Ordinal);

    private readonly Func<string, string, IReadOnlyCollection<string>, CancellationToken, Task<bool>> _delegation;

    public AwsKubernetesPublicRouteVerifier(IOptions<KubernetesPublicDnsOptions> options) : this(
        () => new AmazonRoute53Client(), () => new AmazonSecurityTokenServiceClient(),
        region => new AmazonElasticLoadBalancingV2Client(RegionEndpoint.GetBySystemName(region)),
        new KubernetesPublicDnsDelegation(options.Value).VerifyAsync)
    { }

    internal AwsKubernetesPublicRouteVerifier(Func<IAmazonRoute53> dns,
        Func<IAmazonSecurityTokenService> identity, Func<string, IAmazonElasticLoadBalancingV2> loadBalancer,
        Func<string, string, IReadOnlyCollection<string>, CancellationToken, Task<bool>> delegation)
    {
        // No cloud credential discovery or client construction during startup/discovery.
        _dns = new(dns);
        _identity = new(identity);
        _createLoadBalancer = loadBalancer;
        _delegation = delegation;
    }

    public async Task<bool> VerifyAsync(IReadOnlyDictionary<string, string> parameters, string observedHostname,
        CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("kubernetes.cluster_arn", out var clusterArn) ||
            !parameters.TryGetValue("kubernetes.load_balancer_arn", out var loadBalancerArn) ||
            !parameters.TryGetValue("kubernetes.dns_zone_id", out var zoneId) ||
            !parameters.TryGetValue("kubernetes.dns_record_name", out var recordName) ||
            !parameters.TryGetValue("kubernetes.public_url", out var rawUrl) ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            Normalize(recordName) != Normalize(url.IdnHost) || string.IsNullOrWhiteSpace(zoneId)) return false;
        var cluster = clusterArn.Split(':', 6);
        var lbArn = loadBalancerArn.Split(':', 6);
        if (cluster.Length != 6 || lbArn.Length != 6 || cluster[0] != "arn" || cluster[2] != "eks" ||
            lbArn[0] != "arn" || lbArn[2] != "elasticloadbalancing" || cluster[1] != lbArn[1] ||
            cluster[3] != lbArn[3] || cluster[4] != lbArn[4] || cluster[4].Length != 12 ||
            cluster[4].Any(character => !char.IsAsciiDigit(character))) return false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var identity = await _identity.Value.GetCallerIdentityAsync(new GetCallerIdentityRequest(), bounded.Token).ConfigureAwait(false);
            if (identity.Account != cluster[4] || identity.Arn?.StartsWith("arn:" + cluster[1] + ":", StringComparison.Ordinal) != true) return false;
            var client = _loadBalancers.GetOrAdd(cluster[3], _createLoadBalancer);
            var response = await client.DescribeLoadBalancersAsync(new DescribeLoadBalancersRequest
            { LoadBalancerArns = [loadBalancerArn] }, bounded.Token).ConfigureAwait(false);
            if (response.LoadBalancers is not { Count: 1 }) return false;
            var lb = response.LoadBalancers[0];
            if (lb.LoadBalancerArn != loadBalancerArn || lb.State?.Code?.Value != "active" ||
                lb.Scheme?.Value != "internet-facing" || NormalizeLoadBalancer(lb.DNSName) != NormalizeLoadBalancer(observedHostname) ||
                string.IsNullOrWhiteSpace(lb.CanonicalHostedZoneId)) return false;
            var listeners = await client.DescribeListenersAsync(new DescribeListenersRequest
            { LoadBalancerArn = loadBalancerArn }, bounded.Token).ConfigureAwait(false);
            if (listeners.Listeners is null || !listeners.Listeners.Any(listener => listener.LoadBalancerArn == loadBalancerArn &&
                    listener.Port == url.Port && listener.Protocol?.Value is "TLS" or "HTTPS")) return false;
            var zone = await _dns.Value.GetHostedZoneAsync(new GetHostedZoneRequest { Id = zoneId }, bounded.Token).ConfigureAwait(false);
            if (zone.HostedZone is null || zone.HostedZone.Config?.PrivateZone == true ||
                zone.HostedZone.Id?.Split('/').Last() != zoneId.Split('/').Last()) return false;
            if (string.IsNullOrWhiteSpace(zone.HostedZone.Name) || zone.DelegationSet?.NameServers is not { Count: > 0 } nameServers ||
                !await _delegation(zone.HostedZone.Name, url.IdnHost, nameServers, bounded.Token).ConfigureAwait(false)) return false;
            var query = new ListResourceRecordSetsRequest
            { HostedZoneId = zoneId, StartRecordName = recordName, StartRecordType = RRType.A, MaxItems = "100" };
            var serving = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < 10; page++)
            {
                var records = await _dns.Value.ListResourceRecordSetsAsync(query, bounded.Token).ConfigureAwait(false);
                foreach (var record in records.ResourceRecordSets ?? [])
                {
                    if (Normalize(record.Name) != Normalize(recordName)) continue;
                    if (record.Type?.Value is not ("A" or "AAAA" or "CNAME")) continue;
                    // Refuse routing policies entirely: a matching alias among weighted,
                    // failover, latency or multivalue alternatives is insufficient proof.
                    if (record.Type.Value == "CNAME" || !string.IsNullOrEmpty(record.SetIdentifier) ||
                        record.MultiValueAnswer == true || !string.IsNullOrEmpty(record.HealthCheckId) ||
                        record.AliasTarget is null || record.ResourceRecords is { Count: > 0 } ||
                        NormalizeLoadBalancer(record.AliasTarget.DNSName) != NormalizeLoadBalancer(lb.DNSName) ||
                        record.AliasTarget.HostedZoneId != lb.CanonicalHostedZoneId) return false;
                    if (!serving.Add(record.Type.Value)) return false;
                }
                if (records.IsTruncated != true || Normalize(records.NextRecordName) != Normalize(recordName))
                    return serving.Count is > 0 and <= 2;
                if (string.IsNullOrWhiteSpace(records.NextRecordName) || records.NextRecordType is null) return false;
                query.StartRecordName = records.NextRecordName;
                query.StartRecordType = records.NextRecordType;
                query.StartRecordIdentifier = records.NextRecordIdentifier;
            }
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Amazon.Runtime.AmazonServiceException) { return false; }
        catch (Amazon.Runtime.AmazonClientException) { return false; }
    }

    private static string Normalize(string? hostname) => (hostname ?? "").TrimEnd('.').ToLowerInvariant();

    private static string NormalizeLoadBalancer(string? hostname)
    {
        var normalized = Normalize(hostname);
        return normalized.StartsWith("dualstack.", StringComparison.Ordinal) ? normalized[10..] : normalized;
    }

    public void Dispose()
    {
        if (_dns.IsValueCreated) _dns.Value.Dispose();
        if (_identity.IsValueCreated) _identity.Value.Dispose();
        foreach (var client in _loadBalancers.Values) client.Dispose();
    }
}
