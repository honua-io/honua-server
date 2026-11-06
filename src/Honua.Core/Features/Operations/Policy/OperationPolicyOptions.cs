// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Policy;

public sealed class OperationPolicyOptions
{
    public const string SectionName = "Operations:Policy";

    public bool Enabled { get; set; }

    public PolicyDecisionKind DefaultDecision { get; set; } = PolicyDecisionKind.Allow;

    public string? DefaultReason { get; set; }

    public string? DefaultApprovalLane { get; set; }

    public IList<OperationPolicyRule> Rules { get; set; } = [];

    public bool BuiltInProductionRules { get; set; } = true;
}

public sealed class OperationPolicyRule
{
    public string OperationId { get; set; } = "*";

    public string? Tier { get; set; }

    public string? Role { get; set; }

    public PolicyDecisionKind Decision { get; set; } = PolicyDecisionKind.Allow;

    public string? Reason { get; set; }

    public string? ApprovalLane { get; set; }
}
