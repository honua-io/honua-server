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
    public void AdminApprovalPlan_ReviewerProjection_IdentifiesTargetAndChange()
    {
        var definition = AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == "admin.layer.filter.set");
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(d => d.OperationId == definition.OperationId);
        var mapper = new AdminApiOperationApprovalRequestMapper(definition);
        var proposal = mapper.Map(descriptor, new OperationRequest
        {
            OperationId = definition.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["layerId"] = "123",
                ["permanentFilter"] = """{"expression":"status = 'open'","language":"arcgis-sql"}"""
            }
        }, new OperationPolicyContext { PrincipalId = "requester", TenantId = "tenant-a" },
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

        // ProposalEndpoints.ToDetail exposes these fields, not ExecutionPayload.
        var reviewerText = string.Join("\n", new[] { proposal.Plan!.Summary }
            .Concat(proposal.Plan.Diff).Concat(proposal.Plan.DryRun));
        reviewerText.Should().Contain("123", "the reviewer must be shown the target resource");
        reviewerText.Should().Contain("status = 'open'", "the reviewer must be shown the proposed change");
    }

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

    [Fact]
    public void DryRunReview_IsDistinctFromCommittingReview()
    {
        var definition = AdminOperateOperationCatalog.Definitions.Single(item => item.OperationId == "admin.metadata.prevalidate");
        var descriptor = AdminOperateOperationCatalog.Descriptors.Single(item => item.OperationId == definition.OperationId);
        var mapper = new AdminOperateOperationApprovalRequestMapper(definition);
        var request = new OperationRequest { OperationId = definition.OperationId };
        var context = new OperationPolicyContext { TenantId = "tenant-a", PrincipalId = "requester" };
        var decision = new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval };
        var committing = mapper.Map(descriptor, request, context, decision).Plan!;
        var preview = mapper.Map(descriptor, request with { DryRun = true }, context, decision).Plan!;
        Review(preview).Should().Contain("dry-run").And.NotBe(Review(committing));
    }

    [Fact]
    public void ProposalDetailModels_DoNotExposePrivateExecutionSeal()
    {
        typeof(Honua.Server.Features.Admin.Models.ProposalDetailResponse).GetProperty("SealedPlanHash").Should().BeNull();
        typeof(Honua.Ai.Protocols.Mcp.Models.McpProposalResource).GetProperty("SealedPlanHash").Should().BeNull();
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
