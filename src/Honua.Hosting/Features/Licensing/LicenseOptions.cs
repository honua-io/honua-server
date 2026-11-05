// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

internal sealed class LicenseOptions
{
    public const string SectionName = "Licensing";

    public LicenseMode Mode { get; set; } = LicenseMode.Enabled;

    internal static LicenseMode ParseMode(string? value)
    {
        if (value is null || string.Equals(value.Trim(), nameof(LicenseMode.Enabled), StringComparison.OrdinalIgnoreCase))
        {
            return LicenseMode.Enabled;
        }

        if (string.Equals(value.Trim(), nameof(LicenseMode.Disabled), StringComparison.OrdinalIgnoreCase))
        {
            return LicenseMode.Disabled;
        }

        throw new InvalidOperationException("Licensing:Mode must be Enabled or Disabled. Set an explicit supported mode and restart.");
    }

    public Honua.Core.Features.Licensing.Domain.HonuaEdition? Edition { get; set; }

    public string? LicensePath { get; set; }

    // Inline content wins over a file unless an uploaded override exists, and a secret reference keeps the envelope out of the image.
    public string? LicenseContent { get; set; }

    // A secret reference wins over inline content and a file unless an uploaded override exists, and an unresolvable paid license refuses startup.
    public string? LicenseContentSecretRef { get; set; }

    public Dictionary<string, string> TrustedKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AllowAdminUpload { get; set; }

    public int ExpiryWarningDays { get; set; } = 30;

    // This grants every entitlement up to the named edition without a signed license and must not be set in production.
    public string? DevGrantEdition { get; set; }
}
