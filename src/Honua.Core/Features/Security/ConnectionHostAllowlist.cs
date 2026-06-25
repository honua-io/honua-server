// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Core.Features.Security;

/// <summary>
/// Outcome of a <see cref="IConnectionHostAllowlist"/> check.
/// </summary>
/// <param name="IsAllowed">Whether the host satisfies the configured connection policy.</param>
/// <param name="Reason">A short, client-safe explanation when <paramref name="IsAllowed"/> is <see langword="false"/>; otherwise <see langword="null"/>.</param>
public readonly record struct ConnectionHostDecision(bool IsAllowed, string? Reason)
{
    /// <summary>A decision that permits the host.</summary>
    public static ConnectionHostDecision Allowed() => new(true, null);

    /// <summary>A decision that rejects the host with the supplied client-safe reason.</summary>
    public static ConnectionHostDecision Denied(string reason) => new(false, reason);
}

/// <summary>
/// Validates that an outbound data-source connection host is permitted by the
/// configured connection policy (host allowlist and/or private-address blocking).
/// </summary>
public interface IConnectionHostAllowlist
{
    /// <summary>Whether any restriction is currently enforced.</summary>
    bool IsEnforced { get; }

    /// <summary>
    /// Evaluates whether <paramref name="host"/> may be used as a connection destination.
    /// </summary>
    /// <param name="host">The connection host (hostname or IP literal).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ConnectionHostDecision> EvaluateAsync(string? host, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IConnectionHostAllowlist"/> implementation. Generalizes the
/// outbound-HTTP SSRF guard (#2004) to the data-source connection layer (#354) by
/// reusing <see cref="OutboundHttpUrlValidator.IsPrivateOrReservedAddress(IPAddress)"/>
/// for reserved-range coverage and adding an explicit, operator-configured host allowlist.
/// </summary>
public sealed class ConnectionHostAllowlist : IConnectionHostAllowlist
{
    private readonly ConnectionHostAllowlistOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostResolver;

    /// <summary>
    /// Creates a new allowlist policy bound to the supplied options, using DNS for
    /// host resolution.
    /// </summary>
    /// <param name="options">The configured connection host policy.</param>
    public ConnectionHostAllowlist(ConnectionHostAllowlistOptions options)
        : this(options, static (host, ct) => Dns.GetHostAddressesAsync(host, ct))
    {
    }

    /// <summary>
    /// Creates a new allowlist policy with an explicit host resolver. Intended for tests
    /// that need to drive resolution deterministically without real DNS.
    /// </summary>
    /// <param name="options">The configured connection host policy.</param>
    /// <param name="hostResolver">Resolves a hostname to its candidate addresses.</param>
    public ConnectionHostAllowlist(
        ConnectionHostAllowlistOptions options,
        Func<string, CancellationToken, Task<IPAddress[]>> hostResolver)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
    }

    /// <inheritdoc />
    public bool IsEnforced => _options.IsEnforced;

    /// <inheritdoc />
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

        foreach (var address in addresses)
        {
            if (OutboundHttpUrlValidator.IsPrivateOrReservedAddress(address))
            {
                return true;
            }
        }

        return false;
    }
}
