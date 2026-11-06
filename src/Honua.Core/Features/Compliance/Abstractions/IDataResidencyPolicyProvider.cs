// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

// Egress paths consult this policy on every potential transfer, so a snapshot read must stay cheap.
public interface IDataResidencyPolicyProvider
{
    // The active policy is never null; a deployment with no operator policy gets the default.
    DataResidencyPolicy GetPolicy();

    DataResidencyDecision Evaluate(string region);
}
