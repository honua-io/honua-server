// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Caching;
using Xunit;

namespace Honua.Server.Tests.Infrastructure.Caching;

/// <summary>
/// Tests for ETag service behavior and conditional header handling.
/// </summary>
[Collection("Unit")]
public class ETagServiceTests
{
    private readonly ETagService _service = new();

    [Fact]
    public void ComputeETag_EmptyContent_UsesSha256EmptyHash()
    {
        // Act
        var etag = _service.ComputeETag(ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.Equal("\"47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU\"", etag);
    }

    [Fact]
    public void IsModified_WithWeakMatchingEtag_ReturnsFalse()
    {
        // Arrange
        var current = _service.ComputeETag("content");
        var ifNoneMatch = $"W/{current}";

        // Act
        var modified = _service.IsModified(ifNoneMatch, current);

        // Assert
        Assert.False(modified);
    }

    [Fact]
    public void MatchesPrecondition_WithWeakEtag_DoesNotMatch()
    {
        // Arrange
        var current = _service.ComputeETag("content");
        var weakIfMatch = $"W/{current}";

        // Act
        var weakMatches = _service.MatchesPrecondition(weakIfMatch, current);
        var strongMatches = _service.MatchesPrecondition(current, current);

        // Assert
        Assert.False(weakMatches);
        Assert.True(strongMatches);
    }
}
