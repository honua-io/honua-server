// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal sealed class AdminHumanApprovalEndpointMetadata
{
    public const string CapabilityHeaderName = "X-Honua-Approval-Capability";
    public static readonly AdminHumanApprovalEndpointMetadata Review = new();
    public static readonly AdminHumanApprovalEndpointMetadata Approve = new();

    private AdminHumanApprovalEndpointMetadata() { }
}
