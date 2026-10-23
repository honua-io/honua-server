// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

/// <summary>Canonical authorization outcomes assigned by the operation dispatcher.</summary>
public static class OperationAuthorizationOutcomes
{
    /// <summary>
    /// OAuth scope authority refused the request before operation acceptance. Such a
    /// denied handle has version zero and no durable audit or proposal identity.
    /// </summary>
    public const string ScopeDeniedBeforeAcceptance = "oauth-scope-denied-before-acceptance";
}
