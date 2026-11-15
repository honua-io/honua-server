// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Ai.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.AnalysisContent;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AnalysisContentLayerAccessTests
{
    [UnitTheory]
    [InlineData(AnalysisContentKind.SavedQuery)]
    [InlineData(AnalysisContentKind.AnalysisPackage)]
    public async Task ReadVersion_DeniedLayer_IsNotFoundBeforeAnyDataRead(AnalysisContentKind kind)
    {
        var fixture = new Fixture(kind);

        var error = await Assert.ThrowsAsync<AnalysisContentNotFoundException>(fixture.ReadAsync);

        Assert.Equal("Layer '42' was not found.", error.Message);
        await fixture.Authorizer.Received(1).AuthorizeLayerAsync(
            fixture.Principal, 42, AuthorizationOperation.Query, Arg.Any<CancellationToken>());
        fixture.AssertNoDataRead();
    }

    [UnitTheory]
    [InlineData(AnalysisContentKind.SavedQuery)]
    [InlineData(AnalysisContentKind.AnalysisPackage)]
    public async Task ReadVersion_AuthorizationUnavailable_PropagatesWithoutDataRead(AnalysisContentKind kind)
    {
        var fixture = new Fixture(kind);
        fixture.Authorizer.AuthorizeLayerAsync(fixture.Principal, 42, AuthorizationOperation.Query,
            Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("authority unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(fixture.ReadAsync);

        fixture.AssertNoDataRead();
    }

    [UnitTest]
    public async Task PreviewSavedQuery_MissingLayer_HasTheSameNotFoundResponseAsDeniedLayer()
    {
        var denied = new Fixture(AnalysisContentKind.SavedQuery);
        var missing = new Fixture(AnalysisContentKind.SavedQuery);
        missing.Authorizer.AuthorizeLayerAsync(missing.Principal, 42, AuthorizationOperation.Query,
            Arg.Any<CancellationToken>()).Returns(AccessDecision.Allowed());

        var deniedError = await Assert.ThrowsAsync<AnalysisContentNotFoundException>(denied.ReadAsync);
        var missingError = await Assert.ThrowsAsync<AnalysisContentNotFoundException>(missing.ReadAsync);

        Assert.Equal(deniedError.Message, missingError.Message);
        Assert.Empty(missing.Reader.ReceivedCalls());
    }

    [UnitTest]
    public async Task EstimateVersion_LaterInputDenied_DoesNotReadEarlierInputStatistics()
    {
        var fixture = new Fixture(AnalysisContentKind.AnalysisPackage);
        fixture.Authorizer.AuthorizeLayerAsync(fixture.Principal, 42, AuthorizationOperation.Query,
            Arg.Any<CancellationToken>()).Returns(AccessDecision.Allowed());

        var error = await Assert.ThrowsAsync<AnalysisContentNotFoundException>(fixture.ReadAsync);

        Assert.Equal("Layer '43' was not found.", error.Message);
        fixture.AssertNoDataRead();
    }

    [UnitTest]
    public async Task EstimateVersion_AllInputsAllowed_ReturnsStatistics()
    {
        var fixture = new Fixture(AnalysisContentKind.AnalysisPackage);
        fixture.Authorizer.AuthorizeLayerAsync(fixture.Principal, Arg.Any<int>(), AuthorizationOperation.Query,
            Arg.Any<CancellationToken>()).Returns(AccessDecision.Allowed());
        fixture.Reader.GetEstimatesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new EstimateResult { EstimatedCount = 5 });

        var result = await fixture.Service.EstimateVersionAsync(fixture.Access, CancellationToken.None);

        Assert.Equal(10, result.Estimate.EstimatedInputFeatureCount);
        await fixture.Reader.Received(1).GetEstimatesAsync(42, Arg.Any<CancellationToken>());
        await fixture.Reader.Received(1).GetEstimatesAsync(43, Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        internal ClaimsPrincipal Principal { get; } = new(new ClaimsIdentity("Test"));
        internal ILayerAccessAuthorizer Authorizer { get; } = Substitute.For<ILayerAccessAuthorizer>();
        internal IFeatureReader Reader { get; } = Substitute.For<IFeatureReader>();
        private IAnalysisContentStore Store { get; } = Substitute.For<IAnalysisContentStore>();
        private IMetadataV2GraphProvider Graph { get; } = Substitute.For<IMetadataV2GraphProvider>();
        private AnalysisContentKind Kind { get; }
        internal AnalysisContentVersionAccess Access => new("item", 1, Principal);
        internal AnalysisContentService Service { get; }

        internal Fixture(AnalysisContentKind kind)
        {
            Kind = kind;
            Graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(
                new MetadataV2GraphSnapshot(new MetadataV2Graph(), "test", DateTimeOffset.UtcNow));
            Store.GetVersionAsync("item", 1, Arg.Any<CancellationToken>()).Returns(new AnalysisContentVersion
            {
                ItemId = "item", VersionId = "item:v1", Version = 1, Kind = kind,
                ContentHash = "test", CreatedAt = DateTimeOffset.UtcNow,
                SavedQuery = kind == AnalysisContentKind.SavedQuery ? new SavedQueryContent { LayerId = 42 } : null,
                AnalysisPackage = kind == AnalysisContentKind.AnalysisPackage ? new AnalysisPackageContent
                {
                    Plan = new AnalysisPlan { Steps = [LayerStep(42), LayerStep(43)] }
                } : null
            });
            Service = new AnalysisContentService(Store, Graph, Substitute.For<IQueryProcessor>(), Reader,
                Substitute.For<IGeoprocessingJobService>(), [], TimeProvider.System,
                NullLogger<AnalysisContentService>.Instance, Authorizer);
        }

        internal async Task ReadAsync()
        {
            if (Kind == AnalysisContentKind.SavedQuery)
            {
                await Service.PreviewSavedQueryAsync(Access, null, CancellationToken.None);
                return;
            }

            await Service.EstimateVersionAsync(Access, CancellationToken.None);
        }

        internal void AssertNoDataRead()
        {
            Assert.Empty(Graph.ReceivedCalls());
            Assert.Empty(Reader.ReceivedCalls());
            Assert.DoesNotContain(Store.ReceivedCalls(), call => call.GetMethodInfo().Name == "UpsertArtifactAsync");
        }

        private static AnalysisPlanStep LayerStep(int layerId) => new()
        {
            StepId = $"layer-{layerId}", Kind = AnalysisPlanStepKind.QueryFeatures,
            Inputs = new Dictionary<string, string> { ["layerId"] = layerId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
    }
}
