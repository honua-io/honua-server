// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Trait("Tier", "Fast")]
public sealed class AdminApprovalReviewTests
{
    [Fact]
    public void LayerFilter_ReviewIdentifiesTenantTargetAndExactChange()
    {
        var plan = Map("admin.layer.filter.set", new Dictionary<string, string?>
        {
            ["layerId"] = "123",
            ["permanentFilter"] = """{"expression":"status = 'open'","language":"arcgis-sql","token":"nested-secret"}"""
        });
        var review = Review(plan);
        review.Should().Contain("tenant-a").And.Contain("123").And.Contain("status = 'open'");
        review.Should().Contain("connection-a").And.Contain("service-a").And.NotContain("nested-secret");
        var different = Map("admin.layer.filter.set", new Dictionary<string, string?>
        {
            ["layerId"] = "456",
            ["permanentFilter"] = """{"expression":"status = 'closed'","language":"arcgis-sql"}"""
        });
        Review(different).Should().NotBe(review);
    }

    [Theory]
    [InlineData("admin.metadata.release-packages.create", "title", "Release 2026")]
    [InlineData("admin.connections.delete", "id", "connection-to-delete")]
    public void OtherAdminFamilies_ReviewShowsProposedValues(string operationId, string parameter, string value)
    {
        var plan = Map(operationId, new Dictionary<string, string?> { [parameter] = value });
        Review(plan).Should().Contain(value).And.Contain("tenant-a");
    }

    [Fact]
    public void ConnectionCredentials_ReviewRedactsSecretsAndRetainsReplay()
    {
        var plan = Map("admin.connections.create", new Dictionary<string, string?>
        {
            ["name"] = "warehouse",
            ["secretReference"] = "vault/warehouse",
            ["secretType"] = "Vault",
            ["connectionString"] = "Host=private;Password=hidden-password",
            ["unexpected"] = "hidden-unknown-value"
        });
        var review = Review(plan);
        review.Should().Contain("warehouse").And.Contain("vault/warehouse").And.Contain("[redacted]");
        review.Should().NotContain("never-display-this").And.NotContain("hidden-password").And.NotContain("hidden-unknown-value");
        plan.ExecutionPayload.Should().Contain("vault/warehouse");
    }

    private static OperationProposalPlan Map(string operationId, Dictionary<string, string?> parameters)
    {
        IOperationApprovalRequestMapper mapper;
        IOperationDescriptor descriptor;
        if (AdminApiOperationCatalog.Definitions.SingleOrDefault(item => item.OperationId == operationId) is { } api)
        {
            mapper = new AdminApiOperationApprovalRequestMapper(api);
            descriptor = AdminApiOperationCatalog.Descriptors.Single(item => item.OperationId == operationId);
        }
        else if (AdminOperateOperationCatalog.Definitions.SingleOrDefault(item => item.OperationId == operationId) is { } operate)
        {
            mapper = new AdminOperateOperationApprovalRequestMapper(operate);
            descriptor = AdminOperateOperationCatalog.Descriptors.Single(item => item.OperationId == operationId);
        }
        else
        {
            mapper = new AdminConnectImportApprovalRequestMapper(
                AdminConnectImportOperationCatalog.Definitions.Single(item => item.OperationId == operationId));
            descriptor = AdminConnectImportOperationCatalog.Descriptors.Single(item => item.OperationId == operationId);
        }
        return mapper.Map(descriptor, new OperationRequest
        {
            OperationId = operationId,
            ConnectionId = "connection-a",
            ServiceName = "service-a",
            Parameters = parameters
        }, new OperationPolicyContext { TenantId = "tenant-a", PrincipalId = "requester" },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval }).Plan!;
    }

    private static string Review(OperationProposalPlan plan) =>
        string.Join('\n', new[] { plan.Summary }.Concat(plan.Diff).Concat(plan.DryRun));
}
