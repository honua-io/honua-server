// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal sealed record ScopedJobAuthenticationDependencies(IScopedJobTokenIssuer? Issuer = null, IExecutionJobStore? Jobs = null);

/// <summary>Authenticates only the current running custom job's frozen, attenuated capability.</summary>
internal sealed class ScopedJobTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ScopedJobAuthenticationDependencies dependencies) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ScopedJobAuthentication.HasCandidate(Context))
        {
            return AuthenticateResult.NoResult();
        }

        var ids = Request.Headers[ScopedJobAuthentication.JobIdHeader];
        var authorizations = Request.Headers.Authorization;
        if (ids.Count != 1 || string.IsNullOrWhiteSpace(ids[0]) || ids[0]!.Length > 128 ||
            ids[0] != ids[0]!.Trim() || authorizations.Count != 1 ||
            !System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(authorizations[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            authorization.Parameter is not { Length: 64 } token || !token.All(Uri.IsHexDigit))
        {
            return Reject();
        }

        if (dependencies.Issuer is null || dependencies.Jobs is null)
        {
            return Reject();
        }

        try
        {
            var validation = await dependencies.Issuer.ValidateAsync(token, ids[0]!, Context.RequestAborted).ConfigureAwait(false);
            var job = await dependencies.Jobs.GetAsync(ids[0]!, Context.RequestAborted).ConfigureAwait(false);
            var owner = job?.Audit.CustomCodeOwnerScope;
            if (validation is null || job is null || owner is null ||
                job.Status != ExecutionJobStatus.Running || job.CancellationRequestedAt is not null ||
                job.Spec.Kind != ExecutionJobKind.Geoprocessing || job.Spec.RuntimeProfile != "custom-code" ||
                validation.Attempt <= 0 || validation.Attempt != job.AttemptCount ||
                validation.ExpiresAt <= DateTimeOffset.UtcNow || validation.JobId != job.OperationId ||
                !job.Spec.Parameters.TryGetValue("env.HONUA_JOB_TOKEN", out var current) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(current)) ||
                !string.Equals(validation.Principal.FindFirstValue(ClaimTypes.NameIdentifier), owner.PrincipalId, StringComparison.Ordinal) ||
                !string.Equals(validation.Principal.FindFirstValue("tenant_id"), owner.TenantId, StringComparison.Ordinal))
            {
                return Reject();
            }

            if (Request.Headers.TryGetValue(TenantContextOptions.TenantHeaderName, out var tenant) &&
                (tenant.Count != 1 || !string.Equals(tenant[0], owner.TenantId, StringComparison.Ordinal)))
            {
                return Reject();
            }

            // No owner rehydration: use precisely the issuer's frozen intersection.
            return AuthenticateResult.Success(new AuthenticationTicket(validation.Principal, Scheme.Name));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Neither a missing shared store nor a malformed durable record can turn
            // an explicit capability into anonymous or alternate-credential authority.
            ScopedJobTokenLog.CallbackValidationFailed(Logger, ex);
            return Reject();
        }
    }

    private AuthenticateResult Reject()
    {
        Context.Items[ScopedJobAuthentication.FailureKey] = true;
        Context.User = new ClaimsPrincipal(new ClaimsIdentity());
        return AuthenticateResult.Fail("Invalid scoped job credential.");
    }
}
