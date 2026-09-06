// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Capabilities;
using StackExchange.Redis;

namespace Honua.ControlPlane;

/// <summary>
/// The result of the one startup Redis durability inspection.
/// </summary>
internal sealed record RedisDurabilityAttestationResult(
    RedisDurabilityAttestation? Attestation,
    DurableJobSubstrateCause? FailureCause,
    string? FailureDetail)
{
    public bool Accepted => Attestation is not null;
}

/// <summary>
/// Reads the Redis policy that protects acknowledged control-plane writes.
/// </summary>
internal static class RedisDurabilityAttestor
{
    public static async Task<RedisDurabilityAttestationResult> InspectAsync(
        IConnectionMultiplexer redis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redis);

        try
        {
            var endpoint = redis.GetEndPoints(configuredOnly: true).FirstOrDefault()
                ?? throw new InvalidOperationException("Redis has no configured endpoint.");
            var server = redis.GetServer(endpoint);

            var persistenceInfo = await server.InfoAsync("persistence")
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var persistence = persistenceInfo
                .SelectMany(static section => section)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            var appendOnly = await ReadConfigAsync(server, "appendonly", cancellationToken).ConfigureAwait(false);
            var appendFsync = await ReadConfigAsync(server, "appendfsync", cancellationToken).ConfigureAwait(false);
            var evictionPolicy = await ReadConfigAsync(server, "maxmemory-policy", cancellationToken).ConfigureAwait(false);

            if (!string.Equals(appendOnly, "yes", StringComparison.OrdinalIgnoreCase)
                || !persistence.TryGetValue("aof_enabled", out var aofEnabled)
                || !string.Equals(aofEnabled, "1", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(
                    DurableJobSubstrateCause.RedisPersistenceDisabled,
                    $"appendonly={appendOnly}, aof_enabled={persistence.GetValueOrDefault("aof_enabled", "missing")}");
            }

            if (!string.Equals(appendFsync, "always", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(appendFsync, "everysec", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(
                    DurableJobSubstrateCause.RedisWritePolicyUnsafe,
                    $"appendfsync={appendFsync}");
            }

            if (!string.Equals(evictionPolicy, "noeviction", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(
                    DurableJobSubstrateCause.RedisEvictionPolicyUnsafe,
                    $"maxmemory-policy={evictionPolicy}");
            }

            return new RedisDurabilityAttestationResult(
                new RedisDurabilityAttestation(
                    FormatEndpoint(endpoint),
                    $"aof (appendonly={appendOnly}, aof_enabled={aofEnabled})",
                    $"appendfsync={appendFsync}",
                    evictionPolicy,
                    DateTimeOffset.UtcNow),
                null,
                null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return Reject(DurableJobSubstrateCause.RedisAttestationUnavailable, ex.Message);
        }
    }

    private static async Task<string> ReadConfigAsync(
        IServer server,
        string name,
        CancellationToken cancellationToken)
    {
        var values = await server.ConfigGetAsync(name)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var value = values
            .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Redis CONFIG GET {name} returned no value.");
        }

        return value;
    }

    private static string FormatEndpoint(EndPoint endpoint)
        => endpoint switch
        {
            DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
            IPEndPoint ip when ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                => $"[{ip.Address}]:{ip.Port}",
            IPEndPoint ip => $"{ip.Address}:{ip.Port}",
            _ => endpoint.ToString() ?? "unknown"
        };

    private static RedisDurabilityAttestationResult Reject(
        DurableJobSubstrateCause cause,
        string detail)
        => new(null, cause, detail);
}
