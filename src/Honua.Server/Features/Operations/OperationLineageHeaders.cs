// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal static class OperationLineageHeaders
{
    internal const string OperationInstanceId = "X-Honua-Operation-Instance-Id";
    internal const string AuditId = "X-Honua-Audit-Id";
    internal const string ProposalId = "X-Honua-Proposal-Id";
    internal const string CorrelationId = "X-Correlation-ID";

    internal static void Apply(HttpRequestMessage message, OperationPolicyContext context)
    {
        Add(message, OperationInstanceId, context.OperationInstanceId);
        Add(message, CorrelationId, context.CorrelationId);
        Add(message, AuditId, context.AuditId);
        Add(message, ProposalId, context.ProposalId ?? context.ApprovedProposalId);
    }

    private static void Add(HttpRequestMessage message, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
