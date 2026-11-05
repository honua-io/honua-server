// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal static class OperatorJourneyApproval
{
    public static bool IsDirectExecute(string operationId) => operationId is
        "admin.connections.create" or
        "admin.import.upload-url" or
        "admin.import.upload-object" or
        "admin.layer.publish" or
        "admin.services.access-policy.set";

    public static OperationApprovalModel ForMutation(string operationId, bool mutating) =>
        mutating && !IsDirectExecute(operationId)
            ? OperationApprovalModel.OperatorGate
            : OperationApprovalModel.None;
}
