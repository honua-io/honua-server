// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class AdminAuthConfigResponse
{
    public bool OidcEnabled { get; set; }

    public List<AdminAuthProviderInfo> Providers { get; set; } = [];

    public AdminAuthClientCertificateInfo? ClientCertificates { get; set; }
}

public sealed class AdminAuthProviderInfo
{
    public required string Key { get; set; }

    public required string DisplayName { get; set; }
}

public sealed class AdminAuthClientCertificateInfo
{
    public required string Mode { get; init; }

    public required string EnvironmentId { get; init; }

    public bool RequiredForAdmin { get; init; }

    public bool RequiredForNative { get; init; }

    public string[] SupportedTransports { get; init; } = [];

    public string[] AcceptedIssuerSubjects { get; init; } = [];

    public string[] AcceptedIssuerThumbprints { get; init; } = [];

    public int? ExpirationWarningThresholdDays { get; init; }

    public bool ForwardedCertificateEnabled { get; init; }
}

public sealed class AdminAuthSessionResponse
{
    public bool IsAuthenticated { get; set; }

    public string? ProviderKey { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public List<AdminAuthClaimInfo> Claims { get; set; } = [];
}

// The operator bearer is a short-lived credential the console forwards to the admin API, and it carries the same roles as the cookie session.
public sealed class AdminOperatorBearerResponse
{
    public required string AccessToken { get; init; }

    public string TokenType { get; init; } = "Bearer";

    public DateTimeOffset ExpiresAt { get; init; }

    public long ExpiresIn { get; init; }
}

public sealed class AdminAuthClaimInfo
{
    public required string Type { get; set; }

    public required string Value { get; set; }
}

internal sealed class AdminAuthAuthorizeUrlRequest
{
}

internal sealed class AdminAuthAuthorizeUrlResponse
{
    public required string AuthorizeUrl { get; init; }
}

internal sealed class AdminAuthTokenRequest
{
    public string? GrantType { get; set; }

    public string? Code { get; set; }

    public string? State { get; set; }
}

internal sealed class AdminAuthTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

internal sealed class AdminAuthLogoutUrlResponse
{
    public string? LogoutUrl { get; init; }
}

internal sealed class AdminAuthOidcDiscoveryDocument
{
    [System.Text.Json.Serialization.JsonPropertyName("issuer")]
    public string? Issuer { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("end_session_endpoint")]
    public string? EndSessionEndpoint { get; init; }
}
