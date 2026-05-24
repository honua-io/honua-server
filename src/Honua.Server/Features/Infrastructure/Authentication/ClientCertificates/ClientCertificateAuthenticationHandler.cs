// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateAuthenticationDefaults
{
    public const string AuthenticationScheme = "HonuaClientCertificate";
}

internal sealed class ClientCertificateAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ClientCertificateAuthenticationDependencies dependencies)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private readonly ClientCertificateExtractor _extractor = dependencies?.Extractor ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IClientCertificateValidator _validator = dependencies.Validator;
    private readonly IOptionsMonitor<ClientCertificateAuthenticationOptions> _clientCertificateOptions = dependencies.Options;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (_clientCertificateOptions.CurrentValue.Mode == ClientCertificateAuthenticationMode.Disabled)
        {
            return AuthenticateResult.NoResult();
        }

        var extraction = _extractor.Extract(Context);
        if (extraction.ErrorCode.HasValue)
        {
            var result = ClientCertificateValidationResult.Failure(
                extraction.ErrorCode.Value,
                extraction.ErrorDetail ?? "The client certificate could not be extracted.");
            await RecordFailureAsync(result).ConfigureAwait(false);
            return AuthenticateResult.Fail(ClientCertificateErrorCodes.ToStableCode(extraction.ErrorCode.Value));
        }

        if (!extraction.HasCertificate)
        {
            return AuthenticateResult.NoResult();
        }

        var validation = await ValidateAsync(extraction).ConfigureAwait(false);
        if (!validation.Succeeded || validation.Principal is null)
        {
            await RecordFailureAsync(validation).ConfigureAwait(false);
            return AuthenticateResult.Fail(ClientCertificateErrorCodes.ToStableCode(
                validation.ErrorCode ?? ClientCertificateValidationErrorCode.MissingCertificate));
        }

        ClientCertificateAuthenticationLog.AuthenticationSucceeded(Logger, validation.ProfileId, validation.EnvironmentId);
        await ClientCertificateAudit.RecordAuthenticationAsync(
            Context,
            validation,
            "mtls.login.success",
            Honua.Core.Features.AuditLog.Abstractions.AuditOutcome.Success).ConfigureAwait(false);

        await MaybeLogAndAuditExpirationWarningAsync(validation).ConfigureAwait(false);

        var ticket = new AuthenticationTicket(
            validation.Principal,
            ClientCertificateAuthenticationDefaults.AuthenticationScheme);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var result = ResolveValidationResult() ??
            ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.MissingCertificate,
                "A client certificate is required for this request.",
                environmentId: _clientCertificateOptions.CurrentValue.EnvironmentId);

        return ClientCertificateProblemDetails.Create(Context, result).ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var result = ResolveValidationResult() ??
            ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.InsufficientRbac,
                "The client certificate principal is not authorized for this resource.",
                environmentId: _clientCertificateOptions.CurrentValue.EnvironmentId);

        var forbidden = result.Succeeded
            ? result with
            {
                Succeeded = false,
                ErrorCode = ClientCertificateValidationErrorCode.InsufficientRbac,
                Detail = "The client certificate principal is not authorized for this resource.",
            }
            : result;

        return ClientCertificateProblemDetails.Create(Context, forbidden).ExecuteAsync(Context);
    }

    private async Task<ClientCertificateValidationResult> ValidateAsync(ClientCertificateExtractionResult extraction)
    {
        if (Context.Items.TryGetValue(ClientCertificateHttpContextItems.ValidationResult, out var cached) &&
            cached is ClientCertificateValidationResult cachedResult)
        {
            return cachedResult;
        }

        var validation = await _validator.ValidateAsync(extraction.Certificate, Context.RequestAborted).ConfigureAwait(false);
        Context.Items[ClientCertificateHttpContextItems.ValidationResult] = validation;
        return validation;
    }

    private ClientCertificateValidationResult? ResolveValidationResult()
    {
        if (Context.Items.TryGetValue(ClientCertificateHttpContextItems.ValidationResult, out var cached) &&
            cached is ClientCertificateValidationResult validation)
        {
            return validation;
        }

        if (Context.Items.TryGetValue(ClientCertificateHttpContextItems.ExtractionResult, out var extractionValue) &&
            extractionValue is ClientCertificateExtractionResult extraction &&
            extraction.ErrorCode.HasValue)
        {
            return ClientCertificateValidationResult.Failure(
                extraction.ErrorCode.Value,
                extraction.ErrorDetail ?? "The client certificate could not be extracted.",
                environmentId: _clientCertificateOptions.CurrentValue.EnvironmentId);
        }

        return null;
    }

    private async Task RecordFailureAsync(ClientCertificateValidationResult validation)
    {
        var code = validation.ErrorCode.HasValue
            ? ClientCertificateErrorCodes.ToStableCode(validation.ErrorCode.Value)
            : "client_certificate_invalid";
        ClientCertificateAuthenticationLog.AuthenticationFailed(Logger, code, validation.ProfileId, validation.EnvironmentId);
        await ClientCertificateAudit.RecordAuthenticationAsync(
            Context,
            validation,
            "mtls.login.failure",
            Honua.Core.Features.AuditLog.Abstractions.AuditOutcome.Failure).ConfigureAwait(false);
    }

    private async Task MaybeLogAndAuditExpirationWarningAsync(ClientCertificateValidationResult validation)
    {
        if (!validation.DaysUntilExpiry.HasValue || string.IsNullOrWhiteSpace(validation.ProfileId))
        {
            return;
        }

        var profile = await Context.RequestServices
            .GetRequiredService<IClientCertificateTrustStore>()
            .GetProfileAsync(validation.ProfileId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (profile is null || validation.DaysUntilExpiry > profile.ExpirationWarningThresholdDays)
        {
            return;
        }

        ClientCertificateAuthenticationLog.CertificateExpirationWarning(
            Logger,
            validation.ProfileId,
            validation.EnvironmentId,
            validation.DaysUntilExpiry);
        await ClientCertificateAudit.RecordAuthenticationAsync(
            Context,
            validation,
            "mtls.certificate.expiration_warning",
            Honua.Core.Features.AuditLog.Abstractions.AuditOutcome.Success).ConfigureAwait(false);
    }
}
