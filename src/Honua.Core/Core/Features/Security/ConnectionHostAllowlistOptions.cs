// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security;

// The allowlist is opt-in: an empty host list with private-address blocking off permits every host, and enabling either control tightens registration without requiring the other.
public sealed class ConnectionHostAllowlistOptions
{
    public const string SectionName = "Security:ConnectionAllowlist";

    // A leading-wildcard entry matches subdomains of the suffix and not the bare suffix.
    public IReadOnlyList<string> AllowedHosts { get; set; } = [];

    public bool BlockPrivateAddresses { get; set; }

    public bool IsEnforced => AllowedHosts.Count > 0 || BlockPrivateAddresses;
}
