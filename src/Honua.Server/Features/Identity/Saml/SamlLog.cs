// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Saml;

// The log records the subject and a coarse failure reason, and it never logs the assertion body, the signature, or attribute values.
internal static partial class SamlLog
{
    [LoggerMessage(EventId = 4620, Level = LogLevel.Warning,
        Message = "SAML assertion rejected. Reason={Reason}")]
    public static partial void AssertionRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = 4621, Level = LogLevel.Information,
        Message = "SAML session established for subject '{NameId}' with {RoleCount} role(s).")]
    public static partial void SessionEstablished(ILogger logger, string nameId, int roleCount);

    [LoggerMessage(EventId = 4622, Level = LogLevel.Information,
        Message = "SAML single-logout processed for subject '{NameId}'.")]
    public static partial void SingleLogoutProcessed(ILogger logger, string nameId);

    [LoggerMessage(EventId = 4623, Level = LogLevel.Warning,
        Message = "SAML single-logout request rejected. Reason={Reason}")]
    public static partial void SingleLogoutRejected(ILogger logger, string reason);
}
