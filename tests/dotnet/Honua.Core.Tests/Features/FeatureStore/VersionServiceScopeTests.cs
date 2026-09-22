// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class VersionServiceScopeTests
{
    [Theory]
    [InlineData("service-a", true)]
    [InlineData("service-b", false)]
    [InlineData(null, false)]
    public async Task Resolution_RequiresPersistedAssociation_ForNamesAndGuids(string? associatedService, bool visible)
    {
        var version = Branch(associatedService);
        var manager = new Mock<IVersionManager>(MockBehavior.Strict);
        manager.Setup(instance => instance.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VersionContext.ForVersion(version));
        manager.Setup(instance => instance.GetVersionAsync(version.VersionId, It.IsAny<CancellationToken>())).ReturnsAsync(version);
        foreach (var identity in new[] { "alice.branch", version.VersionId.ToString("D") })
        {
            var result = await manager.Object.ResolveForServiceAsync("service-a", identity);
            result.HasValue.Should().Be(visible);
            if (visible) result!.Value.VersionId.Should().Be(version.VersionId);
        }
    }

    [Fact]
    public async Task ScopedListDoesNotAlterTrustedUnscopedDiagnostics()
    {
        GdbVersion[] versions = [Branch("service-a"), Branch("service-b"), Branch(null)];
        var manager = new Mock<IVersionManager>(MockBehavior.Strict);
        manager.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(versions);
        (await manager.Object.ListForServiceAsync("service-a")).Should().Equal(versions[0]);
        (await manager.Object.ListAsync()).Should().Equal(versions);
    }

    [Fact]
    public async Task DefaultResolutionRemainsNullOverlay_WithoutBranchDescriptorLookup()
    {
        var manager = new Mock<IVersionManager>(MockBehavior.Strict);
        manager.Setup(instance => instance.ResolveAsync("sde.DEFAULT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(VersionContext.Default);
        (await manager.Object.ResolveForServiceAsync("service-a", "sde.DEFAULT")).Should().Be(VersionContext.Default);
        manager.Verify(instance => instance.GetVersionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static GdbVersion Branch(string? service) => new()
    {
        VersionId = Guid.NewGuid(), VersionName = "branch", Owner = "alice", ServiceId = service,
        Access = VersionAccess.Public, State = VersionState.Active, CommonAncestorGeneration = 1,
        BranchGeneration = 1, CreatedAt = DateTimeOffset.UnixEpoch, ModifiedAt = DateTimeOffset.UnixEpoch
    };
}
