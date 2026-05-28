// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed record ClientCertificateValidationResult
{
    public bool Succeeded { get; init; }

    public ClientCertificateValidationErrorCode? ErrorCode { get; init; }

    public string Detail { get; init; } = string.Empty;

    public ClaimsPrincipal? Principal { get; init; }

    public string? ProfileId { get; init; }

    public string? MappingId { get; init; }

    public string? EnvironmentId { get; init; }

    public string? FingerprintSha256 { get; init; }

    public string? IssuerHash { get; init; }

    public int? DaysUntilExpiry { get; init; }

    public string? PrincipalId { get; init; }

    public static ClientCertificateValidationResult Success(
        ClaimsPrincipal principal,
        string profileId,
        string mappingId,
        string environmentId,
        string fingerprintSha256,
        string issuerHash,
        int daysUntilExpiry,
        string principalId)
        => new()
        {
            Succeeded = true,
            Principal = principal,
            ProfileId = profileId,
            MappingId = mappingId,
            EnvironmentId = environmentId,
            FingerprintSha256 = fingerprintSha256,
            IssuerHash = issuerHash,
            DaysUntilExpiry = daysUntilExpiry,
            PrincipalId = principalId,
        };

    public static ClientCertificateValidationResult Failure(
        ClientCertificateValidationErrorCode code,
        string detail,
        string? profileId = null,
        string? environmentId = null,
        string? fingerprintSha256 = null,
        string? issuerHash = null,
        int? daysUntilExpiry = null)
        => new()
        {
            Succeeded = false,
            ErrorCode = code,
            Detail = detail,
            ProfileId = profileId,
            EnvironmentId = environmentId,
            FingerprintSha256 = fingerprintSha256,
            IssuerHash = issuerHash,
            DaysUntilExpiry = daysUntilExpiry,
        };
}

internal enum ClientCertificateValidationErrorCode
{
    MissingCertificate = 0,
    CertificateExpired = 1,
    CertificateNotYetValid = 2,
    UntrustedIssuer = 3,
    UntrustedChain = 4,
    CertificateRevoked = 5,
    WrongEnvironment = 6,
    MissingIdentity = 7,
    UnmappedIdentity = 8,
    InvalidEku = 9,
    ForwardedCertificateUntrusted = 10,
    ForwardedCertificateInvalid = 11,
    InsufficientRbac = 12,
}

internal static class ClientCertificateErrorCodes
{
    public const string MissingCertificate = "client_certificate_missing";
    public const string CertificateExpired = "client_certificate_expired";
    public const string CertificateNotYetValid = "client_certificate_not_yet_valid";
    public const string UntrustedIssuer = "client_certificate_untrusted_issuer";
    public const string UntrustedChain = "client_certificate_untrusted_chain";
    public const string CertificateRevoked = "client_certificate_revoked";
    public const string WrongEnvironment = "client_certificate_wrong_environment";
    public const string MissingIdentity = "client_certificate_missing_identity";
    public const string UnmappedIdentity = "client_certificate_unmapped_identity";
    public const string InvalidEku = "client_certificate_invalid_eku";
    public const string ForwardedCertificateUntrusted = "client_certificate_forwarding_untrusted";
    public const string ForwardedCertificateInvalid = "client_certificate_forwarding_invalid";
    public const string InsufficientRbac = "client_certificate_insufficient_rbac";

    public static string ToStableCode(ClientCertificateValidationErrorCode code) => code switch
    {
        ClientCertificateValidationErrorCode.MissingCertificate => MissingCertificate,
        ClientCertificateValidationErrorCode.CertificateExpired => CertificateExpired,
        ClientCertificateValidationErrorCode.CertificateNotYetValid => CertificateNotYetValid,
        ClientCertificateValidationErrorCode.UntrustedIssuer => UntrustedIssuer,
        ClientCertificateValidationErrorCode.UntrustedChain => UntrustedChain,
        ClientCertificateValidationErrorCode.CertificateRevoked => CertificateRevoked,
        ClientCertificateValidationErrorCode.WrongEnvironment => WrongEnvironment,
        ClientCertificateValidationErrorCode.MissingIdentity => MissingIdentity,
        ClientCertificateValidationErrorCode.UnmappedIdentity => UnmappedIdentity,
        ClientCertificateValidationErrorCode.InvalidEku => InvalidEku,
        ClientCertificateValidationErrorCode.ForwardedCertificateUntrusted => ForwardedCertificateUntrusted,
        ClientCertificateValidationErrorCode.ForwardedCertificateInvalid => ForwardedCertificateInvalid,
        ClientCertificateValidationErrorCode.InsufficientRbac => InsufficientRbac,
        _ => "client_certificate_invalid",
    };

    public static string ToProblemType(ClientCertificateValidationErrorCode code)
        => $"https://honua.io/problems/security/{ToStableCode(code).Replace('_', '-')}";
}
