// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal sealed class ClientCertificateEnforcementMiddleware(
    RequestDelegate next,
    ClientCertificateExtractor extractor,
    IClientCertificateValidator validator,
    IOptionsMonitor<ClientCertificateAuthenticationOptions> options,
    ILogger<ClientCertificateEnforcementMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ClientCertificateExtractor _extractor = extractor;
    private readonly IClientCertificateValidator _validator = validator;
    private readonly IOptionsMonitor<ClientCertificateAuthenticationOptions> _options = options;
    private readonly ILogger<ClientCertificateEnforcementMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await InvokeCoreAsync(context).ConfigureAwait(false);
        }
        finally
        {
            DisposeForwardedCertificate(context);
        }
    }

    private async Task InvokeCoreAsync(HttpContext context)
    {
        var options = _options.CurrentValue;
        if (options.Mode == ClientCertificateAuthenticationMode.Disabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var isRequired = IsRequiredForRequest(context, options);
        var extraction = _extractor.Extract(context);
        if (extraction.ErrorCode.HasValue)
        {
            var result = ClientCertificateValidationResult.Failure(
                extraction.ErrorCode.Value,
                extraction.ErrorDetail ?? "The client certificate could not be extracted.",
                environmentId: options.EnvironmentId);
            await RejectAsync(context, result).ConfigureAwait(false);
            return;
        }

        if (!extraction.HasCertificate)
        {
            if (!isRequired)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var result = ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.MissingCertificate,
                "A client certificate is required for this request.",
                environmentId: options.EnvironmentId);
            await RejectAsync(context, result).ConfigureAwait(false);
            return;
        }

        var validation = await _validator.ValidateAsync(extraction.Certificate, context.RequestAborted).ConfigureAwait(false);
        context.Items[ClientCertificateHttpContextItems.ValidationResult] = validation;
        if (!validation.Succeeded)
        {
            await RejectAsync(context, validation).ConfigureAwait(false);
            return;
        }

        if (isRequired && IsAdminRequest(context, options) && validation.Principal?.IsInRole("admin") != true)
        {
            var forbidden = validation with
            {
                Succeeded = false,
                ErrorCode = ClientCertificateValidationErrorCode.InsufficientRbac,
                Detail = "The client certificate principal is not authorized for the admin surface.",
            };
            await RejectAsync(context, forbidden).ConfigureAwait(false);
            return;
        }

        // Project the validated certificate principal so downstream surfaces that do not
        // run RequireAuthorization (notably native gRPC services) still observe the
        // mapped Honua identity. Later default authentication only overwrites this when
        // it returns a Success result, so API key / OIDC auth can still take precedence.
        if (validation.Principal is not null)
        {
            context.User = validation.Principal;
            ClientCertificateAuthenticationLog.AuthenticationSucceeded(_logger, validation.ProfileId, validation.EnvironmentId);
            await ClientCertificateAudit.RecordAuthenticationAsync(
                context,
                validation,
                "mtls.login.success",
                AuditOutcome.Success).ConfigureAwait(false);
        }

        await _next(context).ConfigureAwait(false);
    }

    private static void DisposeForwardedCertificate(HttpContext context)
    {
        if (context.Items.TryGetValue(ClientCertificateHttpContextItems.ExtractionResult, out var value) &&
            value is ClientCertificateExtractionResult { Source: ClientCertificateSource.ForwardedHeader } extraction)
        {
            extraction.Certificate?.Dispose();
        }
    }

    private async Task RejectAsync(HttpContext context, ClientCertificateValidationResult result)
    {
        var errorCode = result.ErrorCode ?? ClientCertificateValidationErrorCode.MissingCertificate;
        ClientCertificateAuthenticationLog.AuthenticationFailed(
            _logger,
            ClientCertificateErrorCodes.ToStableCode(errorCode),
            result.ProfileId,
            result.EnvironmentId);
        await ClientCertificateAudit.RecordAuthenticationAsync(
            context,
            result,
            "mtls.login.failure",
            errorCode == ClientCertificateValidationErrorCode.InsufficientRbac
                ? AuditOutcome.Denied
                : AuditOutcome.Failure).ConfigureAwait(false);

        await ClientCertificateProblemDetails.Create(context, result)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static bool IsRequiredForRequest(
        HttpContext context,
        ClientCertificateAuthenticationOptions options)
        => options.Mode switch
        {
            ClientCertificateAuthenticationMode.RequiredForAdmin => IsAdminRequest(context, options),
            ClientCertificateAuthenticationMode.RequiredForNative => IsProtectedGrpcRequest(context, options),
            ClientCertificateAuthenticationMode.RequiredForEnvironment =>
                IsAdminRequest(context, options) || IsProtectedGrpcRequest(context, options),
            _ => false,
        };

    private static bool IsAdminRequest(HttpContext context, ClientCertificateAuthenticationOptions options)
    {
        return context.Request.Path.HasValue &&
            options.ProtectedAdminPathPrefixes.Any(prefix =>
                !string.IsNullOrWhiteSpace(prefix) &&
                context.Request.Path.StartsWithSegments(new PathString(prefix.TrimEnd('/'))));
    }

    private static bool IsProtectedGrpcRequest(HttpContext context, ClientCertificateAuthenticationOptions options)
    {
        if (IsGrpcWebRequest(context))
        {
            return false;
        }

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var services = options.ProtectedGrpcServices.Length == 0
            ? ClientCertificateDefaults.ProtectedGrpcServices
            : options.ProtectedGrpcServices;
        return services.Any(service =>
            !string.IsNullOrWhiteSpace(service) &&
            path.StartsWith($"/{service.Trim('/')}/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGrpcWebRequest(HttpContext context)
    {
        // UseGrpcWeb runs ahead of this middleware and rewrites Content-Type from
        // application/grpc-web* to application/grpc, so the captured pre-rewrite flag is
        // the authoritative signal. Fall back to the live request only when the capture
        // middleware was not registered (defensive; integration tests rely on it).
        if (context.Items.TryGetValue(ClientCertificateHttpContextItems.OriginalGrpcWebRequest, out var value) &&
            value is bool wasGrpcWeb)
        {
            return wasGrpcWeb;
        }

        var contentType = context.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("application/grpc-web", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return context.Request.Headers.ContainsKey("X-Grpc-Web");
    }
}

internal static class ClientCertificateDefaults
{
    public static readonly string[] ProtectedGrpcServices =
    [
        "geospatial.v1.FeatureService",
        "geospatial.v1.ProcessService",
        "geospatial.v1.SpecService"
    ];
}
