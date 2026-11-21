// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Server.Tests.Features.Admin;

public sealed class RowLevelSecurityPolicyEnforceabilityTests
{
    [Fact]
    public async Task CreateRlsPolicy_LayerOnProviderWithoutEnforcement_ReturnsBadRequestAndStoresNothing()
    {
        var store = new InMemoryRlsPolicyStore();

        var result = await RlsPolicyEndpoints.HandleCreatePolicy(
            CreateRlsRequest(layer: "roads"),
            store,
            ReadPolicyEnforceabilityTestGraph.CreateChecker(),
            Mock.Of<IFilterExpressionService>(),
            NullLogger<RlsPolicyEndpoints.RlsPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        var badRequest = result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>().Subject;
        badRequest.Value!.Success.Should().BeFalse();
        badRequest.Value.Message.Should().Contain("'roads'").And.Contain("'duckdb'").And.Contain("row-level security");
        (await store.ListPoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRlsPolicy_WildcardScopeCoveringSuchALayer_ReturnsBadRequest()
    {
        var result = await RlsPolicyEndpoints.HandleCreatePolicy(
            CreateRlsRequest(layer: "*"),
            new InMemoryRlsPolicyStore(),
            ReadPolicyEnforceabilityTestGraph.CreateChecker(),
            Mock.Of<IFilterExpressionService>(),
            NullLogger<RlsPolicyEndpoints.RlsPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>();
    }

    [Fact]
    public async Task CreateRlsPolicy_LayerOnEnforcingProvider_IsCreated()
    {
        var store = new InMemoryRlsPolicyStore();

        var result = await RlsPolicyEndpoints.HandleCreatePolicy(
            CreateRlsRequest(layer: "parcels"),
            store,
            ReadPolicyEnforceabilityTestGraph.CreateChecker(),
            Mock.Of<IFilterExpressionService>(),
            NullLogger<RlsPolicyEndpoints.RlsPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        result.Result.Should().BeOfType<Created<ApiResponse<RlsPolicyResponse>>>();
        (await store.ListPoliciesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateFieldMaskPolicy_LayerOnProviderWithoutEnforcement_ReturnsBadRequestAndStoresNothing()
    {
        var store = new InMemoryFieldMaskPolicyStore();

        var result = await FieldMaskPolicyEndpoints.HandleCreatePolicy(
            CreateFieldMaskRequest(layer: "roads"),
            store,
            ReadPolicyEnforceabilityTestGraph.CreateChecker(),
            NullLogger<FieldMaskPolicyEndpoints.FieldMaskPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        var badRequest = result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>().Subject;
        badRequest.Value!.Success.Should().BeFalse();
        badRequest.Value.Message.Should().Contain("'roads'").And.Contain("'duckdb'").And.Contain("field-mask");
        (await store.ListPoliciesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFieldMaskPolicy_LayerOnEnforcingProvider_IsCreated()
    {
        var store = new InMemoryFieldMaskPolicyStore();

        var result = await FieldMaskPolicyEndpoints.HandleCreatePolicy(
            CreateFieldMaskRequest(layer: "parcels"),
            store,
            ReadPolicyEnforceabilityTestGraph.CreateChecker(),
            NullLogger<FieldMaskPolicyEndpoints.FieldMaskPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        result.Result.Should().BeOfType<Created<ApiResponse<FieldMaskPolicyResponse>>>();
        (await store.ListPoliciesAsync()).Should().ContainSingle();
    }

    [Fact]
    public void StartupValidation_PolicyStoresWithTheirSources_Passes()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IRlsPolicyStore, InMemoryRlsPolicyStore>()
            .AddScoped(_ => Mock.Of<IRowLevelSecurityFilterSource>())
            .AddScoped<IFieldMaskPolicyStore, InMemoryFieldMaskPolicyStore>()
            .AddScoped(_ => Mock.Of<IFieldMaskSource>())
            .BuildServiceProvider();

        var act = () => ReadPolicyWiringStartupValidator.Validate(provider.GetRequiredService<IServiceProviderIsService>());

        act.Should().NotThrow();
    }

    [Fact]
    public void StartupValidation_NoPolicyStores_Passes()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => ReadPolicyWiringStartupValidator.Validate(provider.GetRequiredService<IServiceProviderIsService>());

        act.Should().NotThrow();
    }

    [Fact]
    public void StartupValidation_RlsPolicyStoreWithoutRowFilterSource_Throws()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IRlsPolicyStore, InMemoryRlsPolicyStore>()
            .BuildServiceProvider();

        var act = () => ReadPolicyWiringStartupValidator.Validate(provider.GetRequiredService<IServiceProviderIsService>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*IRowLevelSecurityFilterSource*");
    }

    [Fact]
    public async Task StartupValidation_FieldMaskPolicyStoreWithoutFieldMaskSource_FailsHostedServiceStart()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IFieldMaskPolicyStore, InMemoryFieldMaskPolicyStore>()
            .BuildServiceProvider();
        var validator = new ReadPolicyWiringStartupValidator(provider.GetRequiredService<IServiceProviderIsService>());

        var act = () => validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*IFieldMaskSource*");
    }

    private static CreateRlsPolicyRequest CreateRlsRequest(string layer) => new()
    {
        Role = "analyst",
        Service = "*",
        Layer = layer,
        Attribute = "region",
        ClaimType = "region"
    };

    private static CreateFieldMaskPolicyRequest CreateFieldMaskRequest(string layer) => new()
    {
        Role = "analyst",
        Service = "*",
        Layer = layer,
        Attribute = "name"
    };
}
