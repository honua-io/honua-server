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

    /// <summary>
    /// Test/dev only. When set to a valid <c>HonuaEdition</c> (e.g. <c>Pro</c>), every entitlement
    /// up to that edition is granted WITHOUT a signed license, so an out-of-process test/CI server
    /// can exercise edition-gated features such as feature editing. Default off (null); must be set
    /// explicitly. Never set this in production.
    /// </summary>
    public string? DevGrantEdition { get; set; }
}
