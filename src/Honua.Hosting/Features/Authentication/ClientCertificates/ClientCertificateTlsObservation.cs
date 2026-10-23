// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Honua.Infrastructure.Authentication.ClientCertificates;

internal static partial class ClientCertificateTlsObservation
{
    // Kestrel handles absent/unconvertible certificates before invoking this delegate.
    // Its default decision for a present certificate is exactly policyErrors == None.
    internal static void Configure(HttpsConnectionAdapterOptions options, ILogger logger)
    {
        var validator = options.ClientCertificateValidation;
        options.ClientCertificateValidation = (certificate, chain, policyErrors) =>
        {
            // Preserve custom decisions and exceptions; observation is never validation.
            var accepted = validator?.Invoke(certificate, chain, policyErrors)
                ?? policyErrors == SslPolicyErrors.None;
            try
            {
                if (!accepted)
                {
                    var statuses = chain == null
                        ? X509ChainStatusFlags.NoError
                        : chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError,
                            static (flags, status) => flags | status.Status);
                    Rejected(logger, certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
                        policyErrors, statuses, accepted);
                }
            }
            catch (Exception)
            {
                // A broken diagnostic sink must not change the TLS validation decision.
            }

            return accepted;
        };
    }

    [LoggerMessage(EventId = 4147, Level = LogLevel.Warning,
        Message = "TLS client certificate leaf {LeafSha256}, valid {NotBeforeUtc:o} through {NotAfterUtc:o}, policy {PolicyErrors}, chain {ChainStatus}, accepted {Accepted}.")]
    private static partial void Rejected(ILogger logger, string leafSha256,
        DateTime notBeforeUtc, DateTime notAfterUtc, SslPolicyErrors policyErrors,
        X509ChainStatusFlags chainStatus, bool accepted);
}
