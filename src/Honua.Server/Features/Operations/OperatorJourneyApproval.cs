// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Approval assignment for the closed 2026.1 operator roster. These four writes execute for an
/// authorized admin. Every other destructive admin operation stays on the operator gate.
/// </summary>
internal static class OperatorJourneyApproval
{
    /// <summary>Whether MCP must invoke this operation instead of refusing before any work exists.</summary>
    public static bool IsDirectExecute(string operationId) => operationId is
        "admin.connections.create" or
        "admin.import.upload-url" or
        "admin.layer.publish" or
        "admin.services.access-policy.set";

    /// <summary>
    /// Returns <see cref="OperationApprovalModel.None"/> for reads and the closed roster writes,
    /// and <see cref="OperationApprovalModel.OperatorGate"/> for every other mutation.
    /// </summary>
    public static OperationApprovalModel ForMutation(string operationId, bool mutating) =>
        mutating && !IsDirectExecute(operationId)
            ? OperationApprovalModel.OperatorGate
            : OperationApprovalModel.None;
}
