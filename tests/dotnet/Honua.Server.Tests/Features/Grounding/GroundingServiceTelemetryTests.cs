// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using Honua.Ai.Grounding;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// Verifies PA-199: <see cref="GroundingService.GroundAsync"/> starts a
/// "honua.grounding.ground" span on the MCP/AI grounding hot path so a pass
/// through the deterministic engine, authorization filter, and drafting is
/// visible in traces.
/// </summary>
public sealed class GroundingServiceTelemetryTests
{
    [Fact]
    public async Task GroundAsync_SuccessfulPass_StartsGroundingSpan()
    {
        var service = BuildService();
        var request = new GroundingRequest { Goal = "buffer the parcels layer by 100 meters" };

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Honua",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        await service.GroundAsync(request, BuildPrincipal(), CancellationToken.None);

        Assert.Contains(activities, activity => activity.OperationName == "honua.grounding.ground");
    }

    private static GroundingService BuildService()
    {
        var engine = new DeterministicGroundingEngine();
        var catalog = new BuiltInProcessCatalog();
        var authFilter = Substitute.For<IGroundingAuthorizationFilter>();
        authFilter
            .FilterAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyList<GroundingCandidate>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<IReadOnlyList<GroundingCandidate>>(1)));

        var options = Options.Create(new GroundingOptions());
        return new GroundingService(
            engine,
            catalog,
            authFilter,
            options,
            NullLogger<GroundingService>.Instance,
            serviceScopeFactory: null,
            metadataGraphProvider: new EmptyMetadataV2GraphProvider());
    }

    private static ClaimsPrincipal BuildPrincipal()
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private sealed class EmptyMetadataV2GraphProvider : Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider
    {
        private static readonly MetadataV2GraphSnapshot Snapshot = new(
            new MetadataV2Graph(),
            "\"test\"",
            DateTimeOffset.UtcNow);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => new(Snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);
    }
}
