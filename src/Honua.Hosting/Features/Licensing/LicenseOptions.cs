// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Licensing;

internal sealed class LicenseOptions
{
    public const string SectionName = "Licensing";

    /// <summary>
    /// Deployment edition. Declare Pro or Enterprise even when the license source is absent.
    /// Null infers a paid deployment from a configured source; no source means Community.
    /// Explicit Community ignores license sources.
    /// </summary>
    public Honua.Core.Features.Licensing.Domain.HonuaEdition? Edition { get; set; }

    /// <summary>
    /// Configured license file. Successful uploads also atomically persist the authoritative
    /// envelope at this path plus <c>.uploaded</c>. Keep both files on persistent storage.
    /// </summary>
    public string? LicensePath { get; set; }

    /// <summary>
    /// Inline signed license envelope JSON. Unless an uploaded override exists, it takes precedence over
    /// <see cref="LicensePath"/>, so a license can be delivered without a writable filesystem —
    /// e.g. on AWS Lambda / serverless where the image is read-only. Pair it with a secret
    /// reference (<c>Licensing:LicenseContent=aws:secretsmanager:&lt;arn&gt;</c>) so the envelope is
    /// resolved from a secret store at startup rather than baked into the image or env in clear text.
    /// </summary>
    public string? LicenseContent { get; set; }

    /// <summary>
    /// A secret-store reference (for example <c>aws:secretsmanager:&lt;arn&gt;</c>) whose value is the
    /// signed license envelope JSON. When set, an <see cref="ILicenseContentSecretResolver"/> resolves
    /// it at startup and the fetched envelope is validated exactly like <see cref="LicenseContent"/>.
    /// This is the delivery mechanism for serverless/Lambda hosts where the ~2KB envelope does not fit
    /// the platform environment-variable size limit and the filesystem is read-only. It takes precedence
    /// over <see cref="LicenseContent"/> and <see cref="LicensePath"/>, unless an uploaded override exists. If the paid deployment cannot resolve a valid license, startup is refused.
    /// </summary>
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
