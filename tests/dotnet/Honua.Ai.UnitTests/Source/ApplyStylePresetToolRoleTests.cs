// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class ApplyStylePresetToolRoleTests
{
    private const string ServiceId = "svc-parcels";
    private const string PresetId = "preset-dark";

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    public async Task InvokeAsync_AgentWithCustomRoleClaimType_PopulatesAgentRoleInPolicyContext()
    {
        OperationPolicyContext? capturedContext = null;
        var invoker = new CapturingInvoker((_, policyContext) =>
        {
            capturedContext = policyContext;
            return CompletedStyleHandle();
        });

        var context = BuildHttpContext(invoker);
        var tool = new ApplyStylePresetTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<ApplyStylePresetTool>.Instance);
        var args = McpTestFactory.ToArguments(
            new McpApplyStylePresetArgument { ServiceId = ServiceId, ServiceLayerIndex = 0, StyleId = PresetId, DryRun = true },
            MapToolJsonContext.Default.McpApplyStylePresetArgument);

        var result = await tool.InvokeAsync(context, args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        capturedContext.Should().NotBeNull();
        capturedContext!.Roles.Should().Contain(AgentApprovalRule.AgentRole);
    }

    private static OperationHandle CompletedStyleHandle() => new()
    {
        OperationId = "style.apply-preset",
        OperationInstanceId = "op-style-1",
        CorrelationId = "corr-style-1",
        Status = OperationHandleStatus.Completed,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Result = new OperationResultSummary
        {
            Summary = "Applied style preset.",
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["serviceId"] = ServiceId,
                ["serviceLayerIndex"] = "0",
                ["styleId"] = PresetId,
                ["styleVersion"] = "1",
            }
        }
    };

    private static DefaultHttpContext BuildHttpContext(IOperationInvoker invoker)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetStyleAsync(PresetId, Arg.Any<CancellationToken>())
            .Returns(new StyleCatalogRecord { StyleId = PresetId, Title = "Dark", StyleVersion = 1, MapLibreStyleJson = "{}" });

        var services = new ServiceCollection();
        services.AddSingleton(authorization);
        services.AddSingleton(new OperatorApprovalGate(
            Substitute.For<IOperatorAuthorizationEvaluator>(), approval, NullLogger<OperatorApprovalGate>.Instance));
        services.AddSingleton(invoker);
        services.AddSingleton(Substitute.For<IMetadataV2StyleGraphSync>());
        services.AddSingleton(styles);
        services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(BuildGraph()));
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddOptions<RbacOptions>();

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "oidc-agent"),
                    new Claim(ClaimTypes.NameIdentifier, "agent-sub-1"),
                    new Claim("roles", AgentApprovalRule.AgentRole),
                    new Claim("roles", "admin"),
                ],
                authenticationType: "Test",
                nameType: ClaimTypes.Name,
                roleType: "roles"))
        };
    }

    private static MetadataV2Graph BuildGraph()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-parcels", "Parcels")
            .AddStorageBinding("bind-parcels", "res-parcels", "public.parcels", storageLayerId: 42)
            .AddService(ServiceId, "Parcels")
            .AddPublication("pub-parcels", ServiceId, "res-parcels", layerIndex: 0, storageBindingId: "bind-parcels")
            .Build();

    private sealed class CapturingInvoker(Func<OperationRequest, OperationPolicyContext, OperationHandle> handler)
        : IOperationInvoker
    {
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(request, context));
    }
}
