// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal static class OperatorJourneyApproval
{
    // Connection create, update and delete always file a proposal for operator approval.
    public static bool IsDirectExecute(string operationId) => operationId is
        "admin.import.upload-url" or
        "admin.import.upload-object";

    public static OperationApprovalModel ForMutation(string operationId, bool mutating) =>
        mutating && !IsDirectExecute(operationId)
            ? OperationApprovalModel.OperatorGate
            : OperationApprovalModel.None;
}
