// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing.CustomCode;

public enum CustomCodeRepoPolicy
{
    Disabled = 0,

    OrgAllowlist = 1,

    // Accepting any HTTPS repository is for a trusted single-tenant deployment and must not be combined with untrusted submitters.
    Open = 2,

    SignedOnly = 3,
}

internal sealed class CustomCodeOptions
{
    public const string SectionName = "Geoprocessing:CustomCode";

    public CustomCodeRepoPolicy RepoPolicy { get; set; } = CustomCodeRepoPolicy.OrgAllowlist;

    public List<string> RepoAllowlist { get; set; } = [];

    // A tenant allowlist can only narrow the org allowlist, never widen it.
    public Dictionary<string, List<string>> TenantRepoAllowlist { get; set; } = new(StringComparer.Ordinal);

    public List<string> TrustedSignerKeys { get; set; } = [];

    public string? ApiBaseUrl { get; set; }

    public string OutputPrefixRoot { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00", ErrorMessage = "JobTimeout must be between 1 minute and 24 hours")]
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromHours(1);

    // Token expiry adds a short grace past the job timeout so in-flight callbacks can finish and then stops.
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00", ErrorMessage = "TokenGrace must be between 30 seconds and 1 hour")]
    public TimeSpan TokenGrace { get; set; } = TimeSpan.FromMinutes(5);
}
