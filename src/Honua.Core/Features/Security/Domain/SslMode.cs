// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Domain;

/// <summary>
/// SSL connection mode for database connections.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "VerifyCA is kept as a compatibility alias for existing callers while VerifyCa remains the canonical member.")]
public enum SslMode
{
    /// <summary>
    /// SSL is disabled.
    /// </summary>
    Disable = 0,

    /// <summary>
    /// SSL is enabled but not required.
    /// </summary>
    Allow = 1,

    /// <summary>
    /// SSL is preferred if available.
    /// </summary>
    Prefer = 2,

    /// <summary>
    /// SSL is required.
    /// </summary>
    Require = 3,

    /// <summary>
    /// SSL is required with certificate verification.
    /// </summary>
    VerifyCa = 4,

    /// <summary>
    /// Compatibility alias for callers that still use the older casing.
    /// </summary>
    VerifyCA = VerifyCa,

    /// <summary>
    /// SSL is required with full certificate and hostname verification.
    /// </summary>
    VerifyFull = 5
}
