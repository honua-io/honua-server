// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

public sealed class AgentApprovalEntitlementTests
{
    [UnitTheory]
    [InlineData(HonuaEdition.Community, false, LicenseValidationState.Valid)]
    [InlineData(HonuaEdition.Pro, false, LicenseValidationState.Valid)]
    [InlineData(HonuaEdition.Pro, true, LicenseValidationState.Expired)]
    public async Task UnavailableApprovalEntitlement_BlocksPreparationAndActuation(
        HonuaEdition edition, bool active, LicenseValidationState state)
    {
        var entitlements = Substitute.For<ILicenseEntitlementService>();
        entitlements.CheckEntitlement(FeatureCatalog.AiApprovalWorkflowsKey).Returns(new LicenseEntitlementDecision(
            FeatureCatalog.AiApprovalWorkflowsKey, active, edition, state, HonuaEdition.Pro, "Approval entitlement required."));
        await using var services = new ServiceCollection().AddSingleton(entitlements).BuildServiceProvider();
        var style = new SharedStyleEditExecutor(services);
        var workflow = new WorkflowPackageGovernanceExecutor(services);
        var request = new OperationRequest { OperationId = SharedStyleEditOperation.OperationId };
        var context = new OperationPolicyContext { PrincipalId = "agent" };
        Func<Task> prepareStyle = () => style.PrepareAsync(request, context);
        Func<Task> submitStyle = () => style.SubmitAsync(request, context);
        Func<Task> prepareWorkflow = () => workflow.PrepareAsync(request with { OperationId = WorkflowPackageGovernanceOperation.OperationId }, context);
        Func<Task> submitWorkflow = () => workflow.SubmitAsync(request with { OperationId = WorkflowPackageGovernanceOperation.OperationId }, context);
        await prepareStyle.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await submitStyle.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await prepareWorkflow.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await submitWorkflow.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    public void LicenseRevokedAfterPreparation_IsObservedAtActuation()
    {
        var entitlements = Substitute.For<ILicenseEntitlementService>();
        var granted = new LicenseEntitlementDecision(FeatureCatalog.AiApprovalWorkflowsKey, true,
            HonuaEdition.Pro, LicenseValidationState.Valid, HonuaEdition.Pro, string.Empty);
        entitlements.CheckEntitlement(FeatureCatalog.AiApprovalWorkflowsKey).Returns(granted);
        using var services = new ServiceCollection().AddSingleton(entitlements).BuildServiceProvider();
        AgentApprovalEntitlement.Require(services);
        entitlements.CheckEntitlement(FeatureCatalog.AiApprovalWorkflowsKey)
            .Returns(granted with { IsActive = false, UpgradeMessage = "Approval entitlement removed." });
        var act = () => AgentApprovalEntitlement.Require(services);
        act.Should().Throw<GeoprocessingPreconditionFailedException>();
    }
}
