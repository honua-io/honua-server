// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Protocols.Scene;

/// <summary>
/// HMAC signing configuration for short-lived scene access envelopes that
/// authorize CesiumJS-style nested asset cascades on protected scenes.
/// </summary>
/// <remarks>
/// Bind from the <c>Honua:SceneAccessSigning</c> configuration section. The
/// signing key is the only revocation primitive within the token's TTL window:
/// rotating it invalidates every outstanding envelope on the next request.
/// Resolve the value from environment variables or a secret store; never check
/// it into source.
/// </remarks>
internal sealed class SceneAccessSigningOptions
{
    /// <summary>Configuration section name for binding.</summary>
    public const string SectionName = "Honua:SceneAccessSigning";

    /// <summary>
    /// Default token lifetime when <see cref="TokenTtlMinutes"/> is unset.
    /// 15 minutes balances Cesium session length against the credential
    /// exposure window when the token rides on <c>?token=</c>.
    /// </summary>
    public const int DefaultTokenTtlMinutes = 15;

    /// <summary>
    /// Default fraction of the TTL after which clients should refresh.
    /// </summary>
    public const double DefaultRefreshAfterFraction = 0.5;

    /// <summary>
    /// HMAC-SHA256 secret used to sign and verify envelopes. Required for
    /// protected scenes; the issuer/verifier fail closed when this is
    /// unset. Never logged or returned to clients.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Envelope lifetime in minutes. Default <c>15</c>.
    /// </summary>
    [Range(1, 1440)]
    public int TokenTtlMinutes { get; set; } = DefaultTokenTtlMinutes;

    /// <summary>
    /// Fraction of the TTL that must elapse before clients should refresh.
    /// Default <c>0.5</c> (refresh halfway through the lifetime).
    /// </summary>
    [Range(0.0, 1.0)]
    public double RefreshAfterFractionOfTtl { get; set; } = DefaultRefreshAfterFraction;
}
