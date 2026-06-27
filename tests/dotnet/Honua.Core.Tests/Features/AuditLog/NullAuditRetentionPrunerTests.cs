// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Core.Tests.Features.AuditLog;

/// <summary>
/// Unit tests for the no-op retention pruner fallback (#509).
/// </summary>
public sealed class NullAuditRetentionPrunerTests
{
    [Fact]
    public async Task PruneAsync_BoundedPolicy_RemovesNothing()
    {
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(30) };

        var removed = await NullAuditRetentionPruner.Instance.PruneAsync(policy, CancellationToken.None);

        removed.Should().Be(0);
    }

    [Fact]
    public async Task PruneAsync_NullPolicy_Throws()
    {
        var act = async () => await NullAuditRetentionPruner.Instance.PruneAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
