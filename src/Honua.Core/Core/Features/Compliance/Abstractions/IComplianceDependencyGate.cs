// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

// The gate must not report a control as implemented when a prerequisite capability is missing.
public interface IComplianceDependencyGate
{
    bool IsSatisfied(ComplianceDependency dependency);

    string DescribeStatus(ComplianceDependency dependency);
}
