// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Infrastructure.Authentication;

// This bearer is Honua-signed and is not the upstream IdP access token, which never leaves the server; with no usable signing key the feature stays off.
internal sealed class OperatorBearerOptions
{
    public const string SectionName = "Authentication:OperatorBearer";

    internal const int MinimumSigningKeyBytes = 32;
    internal const int DefaultMaxLifetimeMinutes = 30;
    internal const int MaxAllowedLifetimeMinutes = 720;
    internal const string DefaultIssuer = "honua-operator-bearer";
    internal const string DefaultAudience = "honua-admin-api";

    public bool Enabled { get; set; }

    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = DefaultIssuer;

    public string Audience { get; set; } = DefaultAudience;

    // Effective expiry is the earlier of this ceiling and the issuing admin session expiry, so the bearer cannot outlive the session.
    public int MaxLifetimeMinutes { get; set; } = DefaultMaxLifetimeMinutes;

    public bool IsUsable => Enabled && ResolveKeyBytes().Length >= MinimumSigningKeyBytes;

    internal string ResolveIssuer()
        => string.IsNullOrWhiteSpace(Issuer) ? DefaultIssuer : Issuer;

    internal string ResolveAudience()
        => string.IsNullOrWhiteSpace(Audience) ? DefaultAudience : Audience;

    internal int ResolveMaxLifetimeMinutes()
    {
        if (MaxLifetimeMinutes <= 0)
        {
            return DefaultMaxLifetimeMinutes;
        }

        return Math.Min(MaxLifetimeMinutes, MaxAllowedLifetimeMinutes);
    }

    internal byte[] ResolveKeyBytes()
    {
        var resolved = SecretResolver.Resolve(SigningKey) ?? string.Empty;
        return Encoding.UTF8.GetBytes(resolved);
    }
}
