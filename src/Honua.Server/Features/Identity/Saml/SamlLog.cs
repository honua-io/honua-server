// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Saml;

/// <summary>
/// Source-generated structured logging for the SAML 2.0 SP endpoints (#508). Logs the subject
/// identifier and a coarse failure reason for audit; it never logs the assertion body, the
/// signature, or attribute values.
/// </summary>
internal static partial class SamlLog
{
    [LoggerMessage(EventId = 4620, Level = LogLevel.Warning,
        Message = "SAML assertion rejected. Reason={Reason}")]
    public static partial void AssertionRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 4621, Level = LogLevel.Information,
        Message = "SAML session established for subject '{NameId}' with {RoleCount} role(s).")]
    public static partial void SessionEstablished(ILogger logger, string nameId, int roleCount);
}
