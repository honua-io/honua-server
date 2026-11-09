// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class CapabilityManifestEndpointTests
{
    [IntegrationTheory]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task GetManifest_ProposalStoreAvailability_UsesActualStore(bool fromRegistry, bool hasStore)
    {
        await using var fixture = CreateManifestFixture(manifestFromRegistry: fromRegistry)
            .ConfigureServices(services =>
            {
                if (!hasStore)
                {
                    services.RemoveAll<IOperationProposalStore>();
                    services.AddSingleton<IOperationProposalStore, UnavailableOperationProposalStore>();
                }
            });
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.EnsureSuccessStatusCode();
        using var document = await ReadDocumentAsync(response);
        var proposals = GetCapability(document.RootElement, "operations.proposals");
        proposals.GetProperty("available").GetBoolean().Should().Be(hasStore);
        if (hasStore)
        {
            proposals.TryGetProperty("reasonCode", out _).Should().BeFalse();
        }
        else
        {
            proposals.GetProperty("reasonCode").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
        }
        GetCapability(document.RootElement, "jobs.runner").GetProperty("available").GetBoolean().Should().BeFalse();
    }
}
