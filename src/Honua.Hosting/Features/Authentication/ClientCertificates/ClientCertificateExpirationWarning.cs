// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;

internal static class ClientCertificateExpirationWarning
{
    public static async Task TryRecordAsync(
        HttpContext context,
        ClientCertificateValidationResult validation,
        ILogger logger)
    {
        if (!validation.Succeeded ||
            !validation.DaysUntilExpiry.HasValue ||
            string.IsNullOrWhiteSpace(validation.ProfileId))
        {
            return;
        }

        if (context.Items.ContainsKey(ClientCertificateHttpContextItems.ExpirationWarningEmitted))
        {
            return;
        }

        var trustStore = context.RequestServices.GetService<IClientCertificateTrustStore>();
        if (trustStore is null)
        {
            return;
        }

        var profile = await trustStore
            .GetProfileAsync(validation.ProfileId, context.RequestAborted)
            .ConfigureAwait(false);
        if (profile is null || validation.DaysUntilExpiry > profile.ExpirationWarningThresholdDays)
        {
            return;
        }

        context.Items[ClientCertificateHttpContextItems.ExpirationWarningEmitted] = true;
        ClientCertificateAuthenticationLog.CertificateExpirationWarning(
            logger,
            validation.ProfileId,
            validation.EnvironmentId,
            validation.DaysUntilExpiry);
        await ClientCertificateAudit.RecordAuthenticationAsync(
            context,
            validation,
            "mtls.certificate.expiration_warning",
            AuditOutcome.Success).ConfigureAwait(false);
    }
}
