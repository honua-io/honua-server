// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for native/operator client-certificate trust profiles.
/// </summary>
internal static class ClientCertificateAdminEndpoints
{
    internal sealed class ClientCertificateAdminEndpointsLog;

    /// <summary>
    /// Registers client-certificate trust profile management endpoints.
    /// </summary>
    public static void MapClientCertificateAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/security/client-certificates")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Security", "Client Certificates")
            .RequireAdminAuthorization();

        group.MapGet("/profiles", HandleListProfiles)
            .WithDisplayName("List Client Certificate Trust Profiles")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/profiles", HandleCreateProfile)
            .WithDisplayName("Create Client Certificate Trust Profile")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/profiles/{profileId}", HandleGetProfile)
            .WithDisplayName("Get Client Certificate Trust Profile")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/profiles/{profileId}", HandleUpdateProfile)
            .WithDisplayName("Update Client Certificate Trust Profile")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/profiles/{profileId}", HandleDisableProfile)
            .WithDisplayName("Disable Client Certificate Trust Profile")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/profiles/{profileId}/mappings", HandleListMappings)
            .WithDisplayName("List Client Certificate Principal Mappings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/profiles/{profileId}/mappings", HandleCreateMapping)
            .WithDisplayName("Create Client Certificate Principal Mapping")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPut("/profiles/{profileId}/mappings/{mappingId}", HandleUpdateMapping)
            .WithDisplayName("Update Client Certificate Principal Mapping")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapDelete("/profiles/{profileId}/mappings/{mappingId}", HandleDisableMapping)
            .WithDisplayName("Disable Client Certificate Principal Mapping")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapGet("/profiles/{profileId}/revocations", HandleListRevocations)
            .WithDisplayName("List Client Certificate Revocations")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/profiles/{profileId}/revocations", HandleAddRevocation)
            .WithDisplayName("Add Client Certificate Revocation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapDelete("/profiles/{profileId}/revocations/{revocationId}", HandleRemoveRevocation)
            .WithDisplayName("Remove Client Certificate Revocation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapPost("/validate", HandleValidateCertificate)
            .WithDisplayName("Validate Client Certificate")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleListProfiles(
        [FromServices] IClientCertificateTrustStore store,
        CancellationToken cancellationToken)
    {
        var profiles = await store.ListProfilesAsync(null, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<ClientCertificateTrustProfileResponse[]>.CreateSuccess(
            profiles.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> HandleCreateProfile(
        UpsertClientCertificateTrustProfileRequest request,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var profileId = string.IsNullOrWhiteSpace(request.ProfileId)
            ? Guid.NewGuid().ToString("N")
            : request.ProfileId.Trim();
        return await UpsertProfileAsync(profileId, request, context, store, logger, isCreate: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> HandleGetProfile(
        string profileId,
        [FromServices] IClientCertificateTrustStore store,
        CancellationToken cancellationToken)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile is null
            ? TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate trust profile was not found."))
            : TypedResults.Ok(ApiResponse<ClientCertificateTrustProfileResponse>.CreateSuccess(ToResponse(profile)));
    }

    private static async Task<IResult> HandleUpdateProfile(
        string profileId,
        UpsertClientCertificateTrustProfileRequest request,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
        => await UpsertProfileAsync(profileId, request, context, store, logger, isCreate: false, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IResult> UpsertProfileAsync(
        string profileId,
        UpsertClientCertificateTrustProfileRequest request,
        HttpContext context,
        IClientCertificateTrustStore store,
        ILogger logger,
        bool isCreate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(request.EnvironmentId))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("ProfileId and EnvironmentId are required."));
        }

        if (!TryParseRevocationMode(request.ChainRevocationMode, out var revocationMode))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("ChainRevocationMode must be NoCheck, Offline, or Online."));
        }

        if (request.ExpirationWarningThresholdDays < 0 || request.RotationGracePeriodDays < 0)
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(
                "ExpirationWarningThresholdDays and RotationGracePeriodDays cannot be negative."));
        }

        if (request.Enabled &&
            !request.AcceptedIssuerSubjects.Any(static value => !string.IsNullOrWhiteSpace(value)) &&
            !request.AcceptedIssuerThumbprints.Any(static value => !string.IsNullOrWhiteSpace(value)) &&
            !request.CustomTrustAnchorCertificates.Any(static value => !string.IsNullOrWhiteSpace(value)))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(
                "Enabled profiles require at least one accepted issuer subject, accepted issuer thumbprint, or custom trust anchor certificate."));
        }

        if (!TryParseIdentityTypes(request.AllowedSanTypes, out var allowedSanTypes, out var error))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(error));
        }

        if (!TryValidateTrustAnchors(request.CustomTrustAnchorCertificates, out var anchorError))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(anchorError));
        }

        var profile = ClientCertificateTrustProfile.FromOptions(new ClientCertificateTrustProfileOptions
        {
            ProfileId = profileId,
            EnvironmentId = request.EnvironmentId,
            DisplayName = request.DisplayName ?? profileId,
            Enabled = request.Enabled,
            AcceptedIssuerThumbprints = request.AcceptedIssuerThumbprints,
            AcceptedIssuerSubjects = request.AcceptedIssuerSubjects,
            CustomTrustAnchorCertificates = request.CustomTrustAnchorCertificates,
            AllowedSanTypes = allowedSanTypes,
            AllowSubjectFallback = request.AllowSubjectFallback,
            RequireClientAuthenticationEku = request.RequireClientAuthenticationEku,
            RequireChainTrust = request.RequireChainTrust,
            ChainRevocationMode = revocationMode,
            ExpirationWarningThresholdDays = request.ExpirationWarningThresholdDays,
            RotationGracePeriodDays = request.RotationGracePeriodDays,
        });

        var saved = await store.UpsertProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        ClientCertificateAuthenticationLog.TrustProfileChanged(logger, saved.ProfileId, saved.EnvironmentId);
        await ClientCertificateAudit.RecordConfigChangeAsync(
            context,
            isCreate ? "mtls.profile.create" : "mtls.profile.update",
            saved.ProfileId,
            saved.EnvironmentId).ConfigureAwait(false);

        return isCreate
            ? TypedResults.Created(
                $"/api/v1/admin/security/client-certificates/profiles/{saved.ProfileId}",
                ApiResponse<ClientCertificateTrustProfileResponse>.CreateSuccess(ToResponse(saved)))
            : TypedResults.Ok(ApiResponse<ClientCertificateTrustProfileResponse>.CreateSuccess(ToResponse(saved)));
    }

    private static async Task<IResult> HandleDisableProfile(
        string profileId,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var profile = await store.DisableProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate trust profile was not found."));
        }

        ClientCertificateAuthenticationLog.TrustProfileChanged(logger, profile.ProfileId, profile.EnvironmentId);
        await ClientCertificateAudit.RecordConfigChangeAsync(
            context,
            "mtls.profile.disable",
            profile.ProfileId,
            profile.EnvironmentId).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<ClientCertificateTrustProfileResponse>.CreateSuccess(ToResponse(profile)));
    }

    private static async Task<IResult> HandleListMappings(
        string profileId,
        [FromServices] IClientCertificateTrustStore store,
        CancellationToken cancellationToken)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile is null
            ? TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate trust profile was not found."))
            : TypedResults.Ok(ApiResponse<ClientCertificatePrincipalMappingResponse[]>.CreateSuccess(
                profile.PrincipalMappings.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> HandleCreateMapping(
        string profileId,
        UpsertClientCertificatePrincipalMappingRequest request,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var mappingId = string.IsNullOrWhiteSpace(request.MappingId)
            ? Guid.NewGuid().ToString("N")
            : request.MappingId.Trim();
        return await UpsertMappingAsync(profileId, mappingId, request, context, store, logger, isCreate: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> HandleUpdateMapping(
        string profileId,
        string mappingId,
        UpsertClientCertificatePrincipalMappingRequest request,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
        => await UpsertMappingAsync(profileId, mappingId, request, context, store, logger, isCreate: false, cancellationToken)
            .ConfigureAwait(false);

    private static async Task<IResult> UpsertMappingAsync(
        string profileId,
        string mappingId,
        UpsertClientCertificatePrincipalMappingRequest request,
        HttpContext context,
        IClientCertificateTrustStore store,
        ILogger logger,
        bool isCreate,
        CancellationToken cancellationToken)
    {
        if (!TryParseIdentityType(request.MatchType, out var matchType))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("MatchType must be fingerprintSha256, sanUri, sanEmail, sanDns, or subject."));
        }

        var mapping = ClientCertificatePrincipalMapping.FromOptions(new ClientCertificatePrincipalMappingOptions
        {
            MappingId = mappingId,
            MatchType = matchType,
            MatchValue = request.MatchValue,
            PrincipalId = request.PrincipalId,
            DisplayName = request.DisplayName ?? request.PrincipalId,
            Roles = request.Roles,
            TenantId = request.TenantId,
            EnvironmentScopes = request.EnvironmentScopes,
            Enabled = request.Enabled,
            NotBefore = request.NotBefore,
            NotAfter = request.NotAfter,
        });

        var saved = await store.UpsertMappingAsync(profileId, mapping, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate trust profile was not found."));
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        ClientCertificateAuthenticationLog.MappingChanged(logger, profileId, saved.MappingId);
        await ClientCertificateAudit.RecordConfigChangeAsync(
            context,
            isCreate ? "mtls.mapping.create" : "mtls.mapping.update",
            profileId,
            profile?.EnvironmentId,
            saved.MappingId).ConfigureAwait(false);

        return isCreate
            ? TypedResults.Created(
                $"/api/v1/admin/security/client-certificates/profiles/{profileId}/mappings/{saved.MappingId}",
                ApiResponse<ClientCertificatePrincipalMappingResponse>.CreateSuccess(ToResponse(saved)))
            : TypedResults.Ok(ApiResponse<ClientCertificatePrincipalMappingResponse>.CreateSuccess(ToResponse(saved)));
    }

    private static async Task<IResult> HandleDisableMapping(
        string profileId,
        string mappingId,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var mapping = await store.DisableMappingAsync(profileId, mappingId, cancellationToken).ConfigureAwait(false);
        if (mapping is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate principal mapping was not found."));
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        ClientCertificateAuthenticationLog.MappingChanged(logger, profileId, mapping.MappingId);
        await ClientCertificateAudit.RecordConfigChangeAsync(
            context,
            "mtls.mapping.disable",
            profileId,
            profile?.EnvironmentId,
            mapping.MappingId).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<ClientCertificatePrincipalMappingResponse>.CreateSuccess(ToResponse(mapping)));
    }

    private static async Task<IResult> HandleListRevocations(
        string profileId,
        [FromServices] IClientCertificateTrustStore store,
        CancellationToken cancellationToken)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile is null
            ? TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate trust profile was not found."))
            : TypedResults.Ok(ApiResponse<ClientCertificateRevocationEntryResponse[]>.CreateSuccess(
                profile.Revocations.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> HandleAddRevocation(
        string profileId,
        AddClientCertificateRevocationRequest request,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FingerprintSha256) &&
            (string.IsNullOrWhiteSpace(request.Issuer) || string.IsNullOrWhiteSpace(request.SerialNumber)))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure(
                "A revocation requires FingerprintSha256 or both Issuer and SerialNumber."));
        }

        var revocation = ClientCertificateRevocationEntry.FromOptions(new ClientCertificateRevocationEntryOptions
        {
            RevocationId = string.IsNullOrWhiteSpace(request.RevocationId)
                ? Guid.NewGuid().ToString("N")
                : request.RevocationId,
            FingerprintSha256 = request.FingerprintSha256,
            Issuer = request.Issuer,
            SerialNumber = request.SerialNumber,
            Reason = request.Reason,
            Actor = ResolveActor(context),
            RevokedAt = DateTimeOffset.UtcNow,
            Enabled = true,
        });

        var saved = await store.AddRevocationAsync(profileId, revocation, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate trust profile was not found."));
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        ClientCertificateAuthenticationLog.RevocationChanged(logger, profileId);
        await ClientCertificateAudit.RecordConfigChangeAsync(
            context,
            "mtls.certificate.revoke",
            profileId,
            profile?.EnvironmentId,
            saved.RevocationId).ConfigureAwait(false);
        return TypedResults.Created(
            $"/api/v1/admin/security/client-certificates/profiles/{profileId}/revocations/{saved.RevocationId}",
            ApiResponse<ClientCertificateRevocationEntryResponse>.CreateSuccess(ToResponse(saved)));
    }

    private static async Task<IResult> HandleRemoveRevocation(
        string profileId,
        string revocationId,
        HttpContext context,
        [FromServices] IClientCertificateTrustStore store,
        [FromServices] ILogger<ClientCertificateAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        var removed = await store.RemoveRevocationAsync(profileId, revocationId, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return TypedResults.NotFound(ApiResponse<object>.Failure("Client-certificate revocation was not found."));
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        ClientCertificateAuthenticationLog.RevocationChanged(logger, profileId);
        await ClientCertificateAudit.RecordConfigChangeAsync(
            context,
            "mtls.certificate.unrevoke",
            profileId,
            profile?.EnvironmentId,
            revocationId).ConfigureAwait(false);
        return TypedResults.Ok(ApiResponse<object>.SuccessWithMessage("Client-certificate revocation removed."));
    }

    private static async Task<IResult> HandleValidateCertificate(
        ValidateClientCertificateRequest request,
        [FromServices] IClientCertificateValidator validator,
        CancellationToken cancellationToken)
    {
        if (!TryParseForwardedEncoding(request.Encoding, out var encoding))
        {
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Encoding must be pem, urlEncodedPem, or base64Der."));
        }

        ClientCertificateValidationResult result;
        try
        {
            using var certificate = ClientCertificateExtractor.ParseConfiguredCertificate(request.Certificate, encoding);
            result = await validator.ValidateAsync(certificate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            result = ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.ForwardedCertificateInvalid,
                "The supplied certificate could not be parsed.");
        }

        if (result.Succeeded &&
            !string.IsNullOrWhiteSpace(request.ProfileId) &&
            !string.Equals(result.ProfileId, request.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            result = ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.WrongEnvironment,
                "The supplied certificate matched a different trust profile.",
                environmentId: result.EnvironmentId,
                fingerprintSha256: result.FingerprintSha256,
                issuerHash: result.IssuerHash,
                daysUntilExpiry: result.DaysUntilExpiry);
        }

        return TypedResults.Ok(ApiResponse<ValidateClientCertificateResponse>.CreateSuccess(new ValidateClientCertificateResponse
        {
            Valid = result.Succeeded,
            Code = result.Succeeded
                ? "success"
                : ClientCertificateErrorCodes.ToStableCode(result.ErrorCode ?? ClientCertificateValidationErrorCode.MissingCertificate),
            Detail = result.Succeeded ? "The certificate is trusted and mapped." : result.Detail,
            PrincipalId = result.PrincipalId,
            TrustProfileId = result.ProfileId,
            MappingId = result.MappingId,
            EnvironmentId = result.EnvironmentId,
            FingerprintSha256 = result.FingerprintSha256,
            DaysUntilExpiry = result.DaysUntilExpiry,
        }));
    }

    private static ClientCertificateTrustProfileResponse ToResponse(ClientCertificateTrustProfile profile) => new()
    {
        ProfileId = profile.ProfileId,
        EnvironmentId = profile.EnvironmentId,
        DisplayName = profile.DisplayName,
        Enabled = profile.Enabled,
        AcceptedIssuerThumbprints = profile.AcceptedIssuerThumbprints.ToArray(),
        AcceptedIssuerSubjects = profile.AcceptedIssuerSubjects.ToArray(),
        AllowedSanTypes = profile.AllowedSanTypes.Select(ToApiIdentityType).ToArray(),
        AllowSubjectFallback = profile.AllowSubjectFallback,
        RequireClientAuthenticationEku = profile.RequireClientAuthenticationEku,
        RequireChainTrust = profile.RequireChainTrust,
        ChainRevocationMode = profile.ChainRevocationMode.ToString(),
        ExpirationWarningThresholdDays = profile.ExpirationWarningThresholdDays,
        RotationGracePeriodDays = profile.RotationGracePeriodDays,
        Revision = profile.Revision,
        PrincipalMappings = profile.PrincipalMappings.Select(ToResponse).ToArray(),
        Revocations = profile.Revocations.Select(ToResponse).ToArray(),
    };

    private static ClientCertificatePrincipalMappingResponse ToResponse(ClientCertificatePrincipalMapping mapping) => new()
    {
        MappingId = mapping.MappingId,
        MatchType = ToApiIdentityType(mapping.MatchType),
        MatchValue = mapping.MatchValue,
        PrincipalId = mapping.PrincipalId,
        DisplayName = mapping.DisplayName,
        Roles = mapping.Roles.ToArray(),
        TenantId = mapping.TenantId,
        EnvironmentScopes = mapping.EnvironmentScopes.ToArray(),
        Enabled = mapping.Enabled,
        NotBefore = mapping.NotBefore,
        NotAfter = mapping.NotAfter,
        Revision = mapping.Revision,
    };

    private static ClientCertificateRevocationEntryResponse ToResponse(ClientCertificateRevocationEntry revocation) => new()
    {
        RevocationId = revocation.RevocationId,
        FingerprintSha256 = revocation.FingerprintSha256,
        Issuer = revocation.Issuer,
        SerialNumber = revocation.SerialNumber,
        Reason = revocation.Reason,
        RevokedAt = revocation.RevokedAt,
        Actor = revocation.Actor,
        Enabled = revocation.Enabled,
    };

    private static bool TryParseIdentityTypes(
        string[] values,
        out ClientCertificateIdentityType[] result,
        out string error)
    {
        var parsed = new List<ClientCertificateIdentityType>();
        foreach (var value in values.Length == 0 ? ["sanUri", "sanEmail", "sanDns"] : values)
        {
            if (!TryParseIdentityType(value, out var type))
            {
                result = [];
                error = "AllowedSanTypes entries must be fingerprintSha256, sanUri, sanEmail, sanDns, or subject.";
                return false;
            }

            parsed.Add(type);
        }

        result = parsed.Distinct().ToArray();
        error = string.Empty;
        return true;
    }

    private static bool TryParseIdentityType(string value, out ClientCertificateIdentityType result)
    {
        result = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant() switch
        {
            "FINGERPRINTSHA256" => ClientCertificateIdentityType.FingerprintSha256,
            "SANURI" => ClientCertificateIdentityType.SanUri,
            "SANEMAIL" => ClientCertificateIdentityType.SanEmail,
            "SANDNS" => ClientCertificateIdentityType.SanDns,
            "SUBJECT" => ClientCertificateIdentityType.Subject,
            _ => (ClientCertificateIdentityType)(-1),
        };

        return result != (ClientCertificateIdentityType)(-1);
    }

    private static string ToApiIdentityType(ClientCertificateIdentityType type) => type switch
    {
        ClientCertificateIdentityType.FingerprintSha256 => "fingerprintSha256",
        ClientCertificateIdentityType.SanUri => "sanUri",
        ClientCertificateIdentityType.SanEmail => "sanEmail",
        ClientCertificateIdentityType.SanDns => "sanDns",
        ClientCertificateIdentityType.Subject => "subject",
        _ => type.ToString(),
    };

    private static bool TryParseRevocationMode(string value, out X509RevocationMode mode)
        => Enum.TryParse(value, ignoreCase: true, out mode) &&
           mode is X509RevocationMode.NoCheck or X509RevocationMode.Offline or X509RevocationMode.Online;

    private static bool TryValidateTrustAnchors(string[] anchors, out string error)
    {
        for (var index = 0; index < anchors.Length; index++)
        {
            var anchor = anchors[index];
            if (string.IsNullOrWhiteSpace(anchor))
            {
                continue;
            }

            if (!ClientCertificateValidator.TryParseTrustAnchor(anchor, out var parsed))
            {
                error = $"CustomTrustAnchorCertificates[{index}] is not a valid PEM or base64 DER certificate.";
                return false;
            }

            parsed.Dispose();
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseForwardedEncoding(string value, out ForwardedClientCertificateEncoding encoding)
    {
        encoding = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant() switch
        {
            "PEM" => ForwardedClientCertificateEncoding.Pem,
            "URLENCODEDPEM" => ForwardedClientCertificateEncoding.UrlEncodedPem,
            "BASE64DER" => ForwardedClientCertificateEncoding.Base64Der,
            _ => (ForwardedClientCertificateEncoding)(-1),
        };

        return encoding != (ForwardedClientCertificateEncoding)(-1);
    }

    private static string ResolveActor(HttpContext context)
    {
        var principal = context.User;
        return principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            principal.Identity?.Name ??
            "anonymous";
    }
}
