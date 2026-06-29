// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Gate + configuration surface for the real-AWS certification lane (#2164). Unlike the emulated
/// fixtures, this one targets a LIVE AWS account, so it is OFF by default and only reports
/// <see cref="Enabled"/> = true when <c>HONUA_REALAWS_CERT_ENABLED</c> is explicitly set to
/// <c>true</c>. The dedicated <c>real-aws-certification.yml</c> workflow sets that flag (and
/// assumes an OIDC role) only when the maintainer AWS credentials are present; on forks, PRs
/// without secrets, and ordinary local runs the flag is absent so every certification test
/// <c>[SkippableFact]</c>-skips rather than failing or spending money.
///
/// Credentials are resolved by the AWS SDK default chain (OIDC web-identity in CI, the shared
/// <c>~/.aws</c> profile locally) — this fixture deliberately does not handle secrets itself. It
/// only surfaces the master switch, the target <see cref="Region"/>, and a per-run unique
/// <see cref="ResourcePrefix"/> so every resource a certification test creates is namespaced
/// <c>honua-cert-*</c> and can never collide with or clobber pre-existing account infrastructure.
/// </summary>
public sealed class RealAwsCertificationFixture : IAsyncLifetime
{
    /// <summary>Master switch. The lane runs only when this is set to <c>true</c>.</summary>
    public const string EnabledEnvVar = "HONUA_REALAWS_CERT_ENABLED";

    /// <summary>Optional region override for the certification lane.</summary>
    public const string RegionEnvVar = "HONUA_REALAWS_CERT_REGION";

    /// <summary>
    /// True only when the lane is explicitly enabled. Defaults to false so the certification
    /// tests skip everywhere except the gated workflow / an opted-in local run.
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// Target AWS region. Resolves <c>HONUA_REALAWS_CERT_REGION</c> → <c>AWS_REGION</c> →
    /// <c>AWS_DEFAULT_REGION</c> → <c>us-west-2</c> (the Honua substrate region).
    /// </summary>
    public string Region { get; private set; } = "us-west-2";

    /// <summary>
    /// Per-run unique prefix (<c>honua-cert-{8 hex}</c>) applied to every created resource so a
    /// certification run is isolated and traceable, and can never be confused with existing infra.
    /// </summary>
    public string ResourcePrefix { get; private set; } = "honua-cert-unset";

    /// <summary>Resolves the lane configuration from the environment.</summary>
    public Task InitializeAsync()
    {
        var enabledRaw = Environment.GetEnvironmentVariable(EnabledEnvVar);
        Enabled = string.Equals(enabledRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        Region =
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(RegionEnvVar),
                Environment.GetEnvironmentVariable("AWS_REGION"),
                Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"))
            ?? "us-west-2";

        ResourcePrefix = $"honua-cert-{Guid.NewGuid():N}"[..18];

        return Task.CompletedTask;
    }

    /// <summary>No standing resources; certification tests own their own create/teardown.</summary>
    public Task DisposeAsync() => Task.CompletedTask;

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
