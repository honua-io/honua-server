// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace Honua.ControlPlane;

/// <summary>Host/admin configuration, never an operation parameter.</summary>
internal sealed class KubernetesPublicDnsOptions
{
    public string[] Resolvers { get; set; } = [];
}

internal sealed record PublicDnsDelegationProof(IReadOnlyList<(string Owner, string NameServer)> NameServers,
    string? HostAuthorityZone);

/// <summary>Requires actual public delegation, including the hostname's closest zone authority.</summary>
internal sealed class KubernetesPublicDnsDelegation(KubernetesPublicDnsOptions options,
    Func<IPAddress, string, string, CancellationToken, Task<PublicDnsDelegationProof>>? query = null)
{
    private readonly string[] _resolvers = options.Resolvers.ToArray();

    internal async Task<bool> VerifyAsync(string zoneName, string hostname, IReadOnlyCollection<string> expectedNameServers,
        CancellationToken cancellationToken)
    {
        static string Normalize(string value) => value.TrimEnd('.').ToLowerInvariant();
        var zone = Normalize(zoneName);
        var host = Normalize(hostname);
        if (zone.Length == 0 || host != zone && !host.EndsWith("." + zone, StringComparison.Ordinal) ||
            _resolvers.Length is < 1 or > 4 || expectedNameServers.Count is < 1 or > 20) return false;
        var expected = expectedNameServers.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (expected.Count != expectedNameServers.Count || expected.Contains("")) return false;
        try
        {
            foreach (var resolver in _resolvers)
            {
                if (!IPAddress.TryParse(resolver, out var address)) return false;
                var proof = await (query ?? QueryAsync)(address, zone, host, cancellationToken).ConfigureAwait(false);
                if (proof.HostAuthorityZone is null || Normalize(proof.HostAuthorityZone) != zone ||
                    proof.NameServers.Count != expected.Count || proof.NameServers.Any(record => Normalize(record.Owner) != zone) ||
                    !expected.SetEquals(proof.NameServers.Select(record => Normalize(record.NameServer)))) return false;
            }
            return true;
        }
        catch (DnsResponseException) { return false; }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    private static async Task<PublicDnsDelegationProof> QueryAsync(IPAddress resolver, string zone, string hostname,
        CancellationToken cancellationToken)
    {
        var client = new LookupClient(new LookupClientOptions(resolver)
        { UseCache = false, UseTcpOnly = true, Retries = 0, Timeout = TimeSpan.FromSeconds(3), ThrowDnsErrors = true });
        var delegation = await client.QueryAsync(zone, QueryType.NS, QueryClass.IN, cancellationToken).ConfigureAwait(false);
        var authority = await client.QueryAsync(hostname, QueryType.SOA, QueryClass.IN, cancellationToken).ConfigureAwait(false);
        if (delegation.HasError || authority.HasError) return new([], null);
        var names = delegation.Answers.NsRecords().Select(record => (record.DomainName.Value, record.NSDName.Value)).ToArray();
        var zones = authority.Answers.SoaRecords().Concat(authority.Authorities.SoaRecords())
            .Select(record => record.DomainName.Value.TrimEnd('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        return new(names, zones.Length == 1 ? zones[0] : null);
    }
}
