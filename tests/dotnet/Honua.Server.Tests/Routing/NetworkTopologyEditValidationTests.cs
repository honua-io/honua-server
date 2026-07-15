// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Provider-neutral batched topology edit validation contract tests (#2716).
/// </summary>
public sealed class NetworkTopologyEditValidationTests
{
    private const int Srid = 4326;
    private static readonly IReadOnlySet<string> _allowedAttributes = new HashSet<string>(StringComparer.Ordinal) { "cost", "reverse_cost" };

    private static readonly string _validLineString = """{"type":"LineString","coordinates":[[0,0],[1,1]]}""";

    private static NetworkEdgeEdit ValidEdge(string id = "edge-1") => new(
        id,
        "v1",
        "v2",
        _validLineString,
        Srid,
        new Dictionary<string, string?> { ["cost"] = "1.5" });

    private static NetworkTurnRestrictionEdit ValidRestriction(
        string id = "r1",
        string fromEdge = "edge-1",
        string toEdge = "edge-2",
        NetworkTurnRestrictionKind kind = NetworkTurnRestrictionKind.Prohibited,
        double? penalty = null) => new(id, fromEdge, "v2", toEdge, kind, penalty, new Dictionary<string, string?>());

    [Fact]
    public void TryValidateBatch_EmptyBatch_ReturnsFalse()
    {
        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(NetworkTopologyEditBatch.Empty, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("at least one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateBatch_ValidAddEdge_ReturnsTrue()
    {
        var batch = new NetworkTopologyEditBatch([ValidEdge()], [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.True(succeeded);
        Assert.Empty(error);
    }

    [Fact]
    public void TryValidateBatch_DuplicateEdgeIdAcrossAddAndUpdate_ReturnsFalse()
    {
        var batch = new NetworkTopologyEditBatch([ValidEdge("dup")], [ValidEdge("dup")], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("Duplicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateBatch_EdgeIdInBothAddAndDelete_ReturnsFalse()
    {
        var batch = new NetworkTopologyEditBatch([ValidEdge("dup")], [], ["dup"], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
    }

    [Fact]
    public void TryValidateBatch_SridMismatch_ReturnsFalse()
    {
        var edge = ValidEdge() with { Srid = 3857 };
        var batch = new NetworkTopologyEditBatch([edge], [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("srid", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateBatch_DisallowedAttributeKey_ReturnsFalse()
    {
        var edge = ValidEdge() with { Attributes = new Dictionary<string, string?> { ["not_a_cost_column"] = "1" } };
        var batch = new NetworkTopologyEditBatch([edge], [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("allowlisted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateBatch_NonFiniteAttributeValue_ReturnsFalse()
    {
        var edge = ValidEdge() with { Attributes = new Dictionary<string, string?> { ["cost"] = "not-a-number" } };
        var batch = new NetworkTopologyEditBatch([edge], [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("finite numeric", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"type":"Point","coordinates":[0,0]}""")]
    [InlineData("""{"type":"LineString","coordinates":[[0,0]]}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void TryValidateBatch_InvalidGeometry_ReturnsFalse(string geoJson)
    {
        var edge = ValidEdge() with { GeometryGeoJson = geoJson };
        var batch = new NetworkTopologyEditBatch([edge], [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
    }

    [Fact]
    public void TryValidateBatch_ValidMultiLineString_ReturnsTrue()
    {
        var multiLine = """{"type":"MultiLineString","coordinates":[[[0,0],[1,1]],[[2,2],[3,3]]]}""";
        var edge = ValidEdge() with { GeometryGeoJson = multiLine };
        var batch = new NetworkTopologyEditBatch([edge], [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.True(succeeded);
    }

    [Fact]
    public void TryValidateBatch_PenaltyRestrictionWithoutPenalty_ReturnsFalse()
    {
        var restriction = ValidRestriction(kind: NetworkTurnRestrictionKind.Penalty, penalty: null);
        var batch = new NetworkTopologyEditBatch([], [], [], [restriction], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("penalty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateBatch_ProhibitedRestrictionWithPenalty_ReturnsFalse()
    {
        var restriction = ValidRestriction(kind: NetworkTurnRestrictionKind.Prohibited, penalty: 5.0);
        var batch = new NetworkTopologyEditBatch([], [], [], [restriction], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
    }

    [Fact]
    public void TryValidateBatch_NegativePenalty_ReturnsFalse()
    {
        var restriction = ValidRestriction(kind: NetworkTurnRestrictionKind.Penalty, penalty: -1.0);
        var batch = new NetworkTopologyEditBatch([], [], [], [restriction], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
    }

    [Fact]
    public void TryValidateBatch_ValidPenaltyRestriction_ReturnsTrue()
    {
        var restriction = ValidRestriction(kind: NetworkTurnRestrictionKind.Penalty, penalty: 5.0);
        var batch = new NetworkTopologyEditBatch([], [], [], [restriction], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.True(succeeded);
    }

    [Fact]
    public void TryValidateBatch_TooManyEdgeItems_ReturnsFalse()
    {
        var adds = Enumerable.Range(0, NetworkTopologyEditValidation.MaxEdgeItemsPerBatch + 1)
            .Select(i => ValidEdge($"edge-{i}"))
            .ToArray();
        var batch = new NetworkTopologyEditBatch(adds, [], [], [], [], []);

        var succeeded = NetworkTopologyEditValidation.TryValidateBatch(batch, Srid, _allowedAttributes, out var error);

        Assert.False(succeeded);
        Assert.Contains("exceeding", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateStableId_EmptyId_ReturnsFalse()
    {
        Assert.False(NetworkTopologyEditValidation.TryValidateStableId(string.Empty, "edge id", out _));
        Assert.False(NetworkTopologyEditValidation.TryValidateStableId(null, "edge id", out _));
    }

    [Fact]
    public void TryValidateStableId_ControlCharacter_ReturnsFalse()
    {
        Assert.False(NetworkTopologyEditValidation.TryValidateStableId("badid", "edge id", out _));
    }

    [Fact]
    public void TryValidateStableId_ValidId_ReturnsTrue()
    {
        Assert.True(NetworkTopologyEditValidation.TryValidateStableId("edge-123", "edge id", out var error));
        Assert.Empty(error);
    }
}
