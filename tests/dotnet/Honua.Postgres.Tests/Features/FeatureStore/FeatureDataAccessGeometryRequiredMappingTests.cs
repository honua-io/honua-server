// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Postgres.Features.FeatureStore.Services;
using Npgsql;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Unit coverage for the BH-014 fix-forward (#2423, reverted by #2433): a create with no
/// geometry on a layer whose geometry column is non-nullable must surface a clean
/// validation message ("Geometry is required...") instead of a raw provider NOT NULL
/// constraint violation. The mapping is driven by the database's own decision (the
/// constraint actually fired), so it can never wrongly reject a valid attribute-only
/// create on a nullable geometry column (Issue #45).
/// </summary>
public sealed class FeatureDataAccessGeometryRequiredMappingTests
{
    private static PostgresException NotNullViolation(string columnName) =>
        new(
            messageText: $"null value in column \"{columnName}\" violates not-null constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.NotNullViolation,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: "features",
            columnName: columnName,
            dataTypeName: null,
            constraintName: null,
            file: null,
            line: null,
            routine: null);

    [Theory]
    [InlineData("geometry")]
    [InlineData("GEOMETRY")]
    [InlineData("geom")]
    [InlineData("shape")]
    [InlineData("the_geom")]
    public void IsGeometryColumn_RecognizesCommonGeometryColumnNames(string columnName)
    {
        FeatureDataAccess.IsGeometryColumn(columnName).Should().BeTrue();
    }

    [Theory]
    [InlineData("name")]
    [InlineData("attributes")]
    [InlineData("layer_id")]
    [InlineData("")]
    [InlineData(null)]
    public void IsGeometryColumn_RejectsNonGeometryColumns(string? columnName)
    {
        FeatureDataAccess.IsGeometryColumn(columnName).Should().BeFalse();
    }

    [Fact]
    public void IsGeometryNotNullViolation_GeometryColumnNotNull_MapsToClean400()
    {
        // Non-nullable geometry column + null geometry create -> the database raises a
        // NOT NULL violation on the geometry column, which we surface as a clean 400.
        FeatureDataAccess.IsGeometryNotNullViolation(NotNullViolation("geometry")).Should().BeTrue();
    }

    [Fact]
    public void IsGeometryNotNullViolation_NonGeometryColumnNotNull_IsNotMapped()
    {
        // A NOT NULL violation on some other required column is unrelated to geometry and
        // must not be reported as a missing-geometry error.
        FeatureDataAccess.IsGeometryNotNullViolation(NotNullViolation("name")).Should().BeFalse();
    }

    [Fact]
    public void IsGeometryNotNullViolation_UniqueViolationOnGeometry_IsNotMapped()
    {
        var unique = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: "features",
            columnName: "geometry",
            dataTypeName: null,
            constraintName: null,
            file: null,
            line: null,
            routine: null);

        FeatureDataAccess.IsGeometryNotNullViolation(unique).Should().BeFalse();
    }

    [Fact]
    public void GetSafeEditOperationError_GeometryRequired_SurfacesActionableMessage()
    {
        var mapped = new GeometryRequiredForCreateException(NotNullViolation("geometry"));

        FeatureDataAccess.GetSafeEditOperationError(mapped, "Create")
            .Should().Be("Geometry is required for create operation on a spatial layer");
    }

    [Fact]
    public void GetSafeEditOperationError_OtherProviderError_StaysGeneric()
    {
        // A generic NOT NULL violation on an unrelated column is not mapped to the
        // geometry message; it stays a safe generic error (no provider internals leak).
        FeatureDataAccess.GetSafeEditOperationError(NotNullViolation("name"), "Create")
            .Should().Be("Create failed.");
    }

    [Fact]
    public void CreateFailedOperationResult_ServerRejectedStatement_IsKnownNotCommitted()
    {
        var result = FeatureDataAccess.CreateFailedOperationResult(
            NotNullViolation("name"),
            "Create");

        result.IsSuccess.Should().BeFalse();
        result.IsCommitOutcomeUnknown.Should().BeFalse();
    }

    [Fact]
    public void CreateFailedOperationResult_LocalValidationFailure_IsKnownNotCommitted()
    {
        var result = FeatureDataAccess.CreateFailedOperationResult(
            new ValidationException("invalid"),
            "Update",
            objectId: 42);

        result.IsSuccess.Should().BeFalse();
        result.ObjectId.Should().Be(42);
        result.IsCommitOutcomeUnknown.Should().BeFalse();
    }

    [Fact]
    public void CreateFailedOperationResult_TransportFailure_RemainsUnknown()
    {
        var result = FeatureDataAccess.CreateFailedOperationResult(
            new TimeoutException("acknowledgement lost"),
            "Delete",
            objectId: 42);

        result.IsSuccess.Should().BeFalse();
        result.ObjectId.Should().Be(42);
        result.IsCommitOutcomeUnknown.Should().BeTrue();
    }
}
