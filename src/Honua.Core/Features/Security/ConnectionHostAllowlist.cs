// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Core.Features.Security;

public readonly record struct ConnectionHostDecision(bool IsAllowed, string? Reason)
{
    public static ConnectionHostDecision Allowed() => new(true, null);

    public static ConnectionHostDecision Denied(string reason) => new(false, reason);
}

public interface IConnectionHostAllowlist
{
    bool IsEnforced { get; }

    Task<ConnectionHostDecision> EvaluateAsync(string? host, CancellationToken cancellationToken = default);
}

public sealed class ConnectionHostAllowlist : IConnectionHostAllowlist
{
    private readonly ConnectionHostAllowlistOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostResolver;

    public ConnectionHostAllowlist(ConnectionHostAllowlistOptions options)
        : this(options, static (host, ct) => Dns.GetHostAddressesAsync(host, ct))
    {
    }

    public ConnectionHostAllowlist(
        ConnectionHostAllowlistOptions options,
        Func<string, CancellationToken, Task<IPAddress[]>> hostResolver)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
    }

    public bool IsEnforced => _options.IsEnforced;

    public async Task<ConnectionHostDecision> EvaluateAsync(
        string? host,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsEnforced)
        {
            return ConnectionHostDecision.Allowed();
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return ConnectionHostDecision.Denied("Connection host is required when a host allowlist is enforced.");
        }

        var trimmedHost = host.Trim();

        if (_options.AllowedHosts.Count > 0 && !IsHostAllowlisted(trimmedHost))
        {
            return ConnectionHostDecision.Denied(
                "Connection host is not in the configured allowlist of permitted database destinations.");
        }

        if (_options.BlockPrivateAddresses &&
            await ResolvesToReservedAddressAsync(trimmedHost, cancellationToken).ConfigureAwait(false))
        {
            return ConnectionHostDecision.Denied(
                "Connection host resolves to a private, loopback, or otherwise reserved network address, which is not allowed by policy.");
        }

        return ConnectionHostDecision.Allowed();
    }

    private bool IsHostAllowlisted(string host)
    {
        foreach (var entry in _options.AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var pattern = entry.Trim();

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                // Leading-wildcard suffix: "*.example.com" matches "db.example.com" but
                // not the bare "example.com" (mirrors browser/TLS wildcard semantics).
                var suffix = pattern[1..]; // ".example.com"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    host.Length > suffix.Length)
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ResolvesToReservedAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (OutboundHttpUrlValidator.IsLocalhostHostName(host))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return OutboundHttpUrlValidator.IsPrivateOrReservedAddress(literal);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _hostResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Fail closed: an unresolvable host cannot be vetted, so treat it as reserved.
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (addresses.Length == 0)
        {
            return true;
        }

        return addresses.Any(OutboundHttpUrlValidator.IsPrivateOrReservedAddress);
    }
}
