// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Configuration;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

internal static class GeoservicesCredentialResolution
{
    public static string? ValidateDiscoveryCredentialRequest(
        GeoservicesCredentialDescriptor? credentials,
        IServiceProvider serviceProvider)
        => ValidateCredentialRequest(credentials, serviceProvider, requireSecretReferences: false);

    public static string? ValidateQueuedCredentialRequest(
        GeoservicesCredentialDescriptor? credentials,
        IServiceProvider serviceProvider)
        => ValidateCredentialRequest(credentials, serviceProvider, requireSecretReferences: true);

    private static string? ValidateCredentialRequest(
        GeoservicesCredentialDescriptor? credentials,
        IServiceProvider serviceProvider,
        bool requireSecretReferences)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (credentials == null)
        {
            return null;
        }

        var mode = credentials.GetNormalizedMode();
        if (!IsKnownExplicitMode(credentials.Mode, mode))
        {
            return "GeoServices credential mode must be token, oauth, or basic.";
        }

        if (!credentials.HasCredentialMaterial && mode == GeoservicesAuthenticationModes.Anonymous)
        {
            return null;
        }

        if (credentials.HasPlaintextSecret && requireSecretReferences)
        {
            return "GeoServices credential secrets must be provided as secret references for queued imports.";
        }

        switch (mode)
        {
            case GeoservicesAuthenticationModes.Token:
            case GeoservicesAuthenticationModes.OAuth:
                if (requireSecretReferences && string.IsNullOrWhiteSpace(credentials.AccessTokenSecretReference))
                {
                    return "Token and OAuth GeoServices imports require accessTokenSecretReference.";
                }

                if (!requireSecretReferences &&
                    string.IsNullOrWhiteSpace(credentials.AccessToken) &&
                    string.IsNullOrWhiteSpace(credentials.AccessTokenSecretReference))
                {
                    return "Token and OAuth GeoServices discovery requires accessToken or accessTokenSecretReference.";
                }

                break;

            case GeoservicesAuthenticationModes.Basic:
                if (string.IsNullOrWhiteSpace(credentials.Username))
                {
                    return "Basic GeoServices credentials require username.";
                }

                if (requireSecretReferences && string.IsNullOrWhiteSpace(credentials.PasswordSecretReference))
                {
                    return "Basic GeoServices imports require username and passwordSecretReference.";
                }

                if (!requireSecretReferences &&
                    string.IsNullOrWhiteSpace(credentials.Password) &&
                    string.IsNullOrWhiteSpace(credentials.PasswordSecretReference))
                {
                    return "Basic GeoServices discovery requires password or passwordSecretReference.";
                }

                break;

            case GeoservicesAuthenticationModes.Anonymous:
                return "Anonymous GeoServices credentials must not include credential material.";

            default:
                return "GeoServices credential mode must be token, oauth, or basic.";
        }

        return RequestSecretReferenceValidation.Validate(
                serviceProvider, credentials.AccessTokenSecretReference, "AccessTokenSecretReference")
            ?? RequestSecretReferenceValidation.Validate(
                serviceProvider, credentials.PasswordSecretReference, "PasswordSecretReference");
    }

    private static bool IsKnownExplicitMode(string? requestedMode, string normalizedMode)
    {
        if (string.IsNullOrWhiteSpace(requestedMode))
        {
            return true;
        }

        return normalizedMode != GeoservicesAuthenticationModes.Anonymous ||
               string.Equals(requestedMode.Trim(), GeoservicesAuthenticationModes.Anonymous, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<GeoservicesDiscoveryRequest> ResolveSecretReferencesAsync(
        GeoservicesDiscoveryRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentials = await ResolveSecretReferencesAsync(
            request.Credentials,
            serviceProvider,
            cancellationToken).ConfigureAwait(false);

        return ReferenceEquals(credentials, request.Credentials)
            ? request
            : request with { Credentials = credentials };
    }

    public static async Task<GeoservicesImportRequest> ResolveSecretReferencesAsync(
        GeoservicesImportRequest request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentials = await ResolveSecretReferencesAsync(
            request.Credentials,
            serviceProvider,
            cancellationToken).ConfigureAwait(false);

        return ReferenceEquals(credentials, request.Credentials)
            ? request
            : request with { Credentials = credentials };
    }

    private static async Task<GeoservicesCredentialDescriptor?> ResolveSecretReferencesAsync(
        GeoservicesCredentialDescriptor? credentials,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (credentials == null)
        {
            return null;
        }

        var resolvedToken = credentials.AccessToken;
        if (!string.IsNullOrWhiteSpace(credentials.AccessTokenSecretReference))
        {
            resolvedToken = await ResolveRequiredSecretAsync(
                serviceProvider,
                credentials.AccessTokenSecretReference,
                "ArcGIS access token",
                cancellationToken).ConfigureAwait(false);
        }

        var resolvedPassword = credentials.Password;
        if (!string.IsNullOrWhiteSpace(credentials.PasswordSecretReference))
        {
            resolvedPassword = await ResolveRequiredSecretAsync(
                serviceProvider,
                credentials.PasswordSecretReference,
                "ArcGIS Basic password",
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(resolvedToken, credentials.AccessToken, StringComparison.Ordinal) &&
            string.Equals(resolvedPassword, credentials.Password, StringComparison.Ordinal))
        {
            return credentials;
        }

        return credentials with
        {
            AccessToken = resolvedToken,
            Password = resolvedPassword
        };
    }

    private static Task<string> ResolveRequiredSecretAsync(
        IServiceProvider serviceProvider,
        string secretReference,
        string secretDescription,
        CancellationToken cancellationToken)
        => RequestSecretReferenceValidation.ResolveRequiredAsync(
            serviceProvider,
            secretReference,
            secretDescription,
            cancellationToken);
}
