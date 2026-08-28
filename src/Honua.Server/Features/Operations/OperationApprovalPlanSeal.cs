// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Operations;

internal static class OperationApprovalPlanSeal
{
    public static string Compute(OperationProposalPlan plan)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            plan,
            Honua.ControlPlane.OperationProposalJsonContext.Default.OperationProposalPlan);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
