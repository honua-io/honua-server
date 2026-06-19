// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

internal sealed class LicenseOptions
{
    public const string SectionName = "Licensing";

    public string? LicensePath { get; set; }

    /// <summary>
    /// Inline signed license envelope JSON. When set (non-empty) it takes precedence over
    /// <see cref="LicensePath"/>, so a license can be delivered without a writable filesystem —
    /// e.g. on AWS Lambda / serverless where the image is read-only. Pair it with a secret
    /// reference (<c>Licensing:LicenseContent=aws:secretsmanager:&lt;arn&gt;</c>) so the envelope is
    /// resolved from a secret store at startup rather than baked into the image or env in clear text.
    /// </summary>
    public string? LicenseContent { get; set; }

    /// <summary>
    /// A reference to a cloud secret holding the signed license envelope JSON, resolved at startup
    /// via an <see cref="ILicenseContentSecretResolver"/> and then treated exactly like
    /// <see cref="LicenseContent"/> (the resolved value becomes the inline envelope). Lets the
    /// license be delivered from a secret store on a read-only/serverless filesystem without baking
    /// it into the image. Azure form:
    /// <c>azure:keyvault:https://&lt;vault&gt;.vault.azure.net/&lt;secret&gt;</c> (managed identity).
    /// </summary>
    /// <remarks>
    /// PROVISIONAL draft (#1745) pending the canonical resolver seam in honua-server#1742. When
    /// both <see cref="LicenseContent"/> and this ref are set, the explicit inline content wins.
    /// Resolution is FAIL-SAFE: any error falls back to Community licensing, never a crash.
    /// </remarks>
    public string? LicenseContentSecretRef { get; set; }

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
