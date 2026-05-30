// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

internal sealed class LicenseOptions
{
    public const string SectionName = "Licensing";

    public string? LicensePath { get; set; }

    public Dictionary<string, string> TrustedKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AllowAdminUpload { get; set; }

    public int ExpiryWarningDays { get; set; } = 30;
}
