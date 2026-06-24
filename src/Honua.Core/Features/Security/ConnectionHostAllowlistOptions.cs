// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security;

/// <summary>
/// Configuration for the outbound data-source connection host allowlist.
/// </summary>
/// <remarks>
/// <para>
/// Bound from the <c>Security:ConnectionAllowlist</c> configuration section. The policy
/// governs which database destinations an administrator may register as a secure
/// connection, generalizing the outbound-HTTP SSRF guard pattern (#2004) to the
/// data-source connection layer (#354).
/// </para>
/// <para>
/// The allowlist is opt-in: when <see cref="AllowedHosts"/> is empty and
/// <see cref="BlockPrivateAddresses"/> is <see langword="false"/>, every host is
/// permitted (preserving the prior, unrestricted behaviour). Enabling either control
/// tightens registration without requiring the other.
/// </para>
/// </remarks>
public sealed class ConnectionHostAllowlistOptions
{
    /// <summary>The configuration section that binds to this options type.</summary>
    public const string SectionName = "Security:ConnectionAllowlist";

    /// <summary>
    /// Hosts that may be used as connection destinations. Each entry is matched
    /// case-insensitively against the connection host. Entries may be:
    /// <list type="bullet">
    /// <item><description>An exact hostname (e.g. <c>db.internal.example.com</c>).</description></item>
    /// <item><description>An exact IPv4/IPv6 literal (e.g. <c>10.0.1.5</c>).</description></item>
    /// <item><description>A leading-wildcard suffix (e.g. <c>*.rds.amazonaws.com</c>), which
    /// matches any subdomain of the suffix but not the bare suffix itself.</description></item>
    /// </list>
    /// When empty, host matching is not enforced (any host is allowed unless blocked by
    /// <see cref="BlockPrivateAddresses"/>).
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// When <see langword="true"/>, connection hosts that are (or resolve to) private,
    /// loopback, link-local, multicast, or otherwise reserved addresses are rejected,
    /// reusing the same range coverage as the outbound-HTTP SSRF guard. Defaults to
    /// <see langword="false"/> so that local/loopback databases remain usable unless an
    /// operator explicitly opts in.
    /// </summary>
    public bool BlockPrivateAddresses { get; set; }

    /// <summary>
    /// Indicates whether any allowlist control is active. When <see langword="false"/>
    /// the policy permits every host and short-circuits DNS resolution.
    /// </summary>
    public bool IsEnforced => AllowedHosts.Count > 0 || BlockPrivateAddresses;
}
