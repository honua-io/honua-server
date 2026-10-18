// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Licensing;

namespace Honua.Server.Features.Operations;

/// <summary>Checks the live entitlement for explicitly governed agent operations.</summary>
internal static class AgentApprovalEntitlement
{
    internal static void Require(IServiceProvider services)
    {
        var decision = LicenseGate.CheckEntitlement(services, FeatureCatalog.AiApprovalWorkflowsKey);
        if (!decision.IsActive)
            throw new GeoprocessingPreconditionFailedException(decision.UpgradeMessage);
    }
}
