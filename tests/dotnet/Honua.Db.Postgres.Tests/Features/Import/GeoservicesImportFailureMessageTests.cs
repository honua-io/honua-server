// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Db.Postgres.Features.Migration;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// #4827 REQ-003: a failed ArcGIS import reports a specific, stable reason the operator can act on,
/// and never echoes the raw exception text, which can carry SQL, hosts, tokens or credentials.
/// </summary>
public sealed class GeoservicesImportFailureMessageTests
{
    private const string Secret = "s3cr3t-value";

    [Fact]
    public void BuildImportFailureMessage_GeometryRejectedByDatabase_NamesGeometryAndSqlStateOnly()
    {
        var exception = new PostgresException(
            $"can not mix dimensionality in a geometry near '{Secret}'", "ERROR", "ERROR", "XX000");

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.TransferringFeatures);

        message.Should().StartWith("ARCGIS_IMPORT_GEOMETRY_REJECTED:");
        message.Should().Contain("SQLSTATE XX000");
        message.Should().Contain("Z/M");
        message.Should().Contain("No imported data was committed");
        message.Should().NotContain(Secret).And.NotContain("can not mix");
    }

    [Theory]
    [InlineData("22P02", "ARCGIS_IMPORT_DATABASE_ERROR:")]
    [InlineData("23505", "ARCGIS_IMPORT_DATABASE_ERROR:")]
    [InlineData("42701", "ARCGIS_IMPORT_DATABASE_ERROR:")]
    [InlineData("57P01", "ARCGIS_IMPORT_DATABASE_UNAVAILABLE:")]
    [InlineData("08006", "ARCGIS_IMPORT_DATABASE_UNAVAILABLE:")]
    [InlineData("53300", "ARCGIS_IMPORT_DATABASE_UNAVAILABLE:")]
    public void BuildImportFailureMessage_DatabaseError_ReportsSqlStateWithoutMessageText(string sqlState, string expectedPrefix)
    {
        var exception = new PostgresException(
            $"relation \"secret_schema\".\"t\" rejected value '{Secret}'", "ERROR", "ERROR", sqlState);

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.CreatingTable);

        message.Should().StartWith(expectedPrefix);
        message.Should().Contain($"SQLSTATE {sqlState}");
        message.Should().Contain("creating the target table");
        message.Should().NotContain(Secret).And.NotContain("secret_schema");
    }

    [Fact]
    public void BuildImportFailureMessage_LostSessionWrappedByCleanup_ClassifiesTheOriginatingDatabaseFailure()
    {
        // Npgsql reports a terminated session by throwing from the disposed transaction, wrapping the
        // server's FATAL error; the job must report the lost session, not the wrapper.
        var exception = new ObjectDisposedException(
            "NpgsqlTransaction",
            new PostgresException("terminating connection due to administrator command", "FATAL", "FATAL", "57P01"));

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.TransferringFeatures);

        message.Should().StartWith("ARCGIS_IMPORT_DATABASE_UNAVAILABLE:");
        message.Should().Contain("SQLSTATE 57P01");
        message.Should().NotContain("terminating connection").And.NotContain("NpgsqlTransaction");
    }

    [Fact]
    public void BuildImportFailureMessage_ConnectionFailure_DoesNotEchoConnectionDetails()
    {
        var exception = new NpgsqlException($"Failed to connect to db.internal:5432 with Password={Secret}");

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.TransferringFeatures);

        message.Should().StartWith("ARCGIS_IMPORT_DATABASE_UNAVAILABLE:");
        message.Should().NotContain(Secret).And.NotContain("db.internal");
    }

    [Fact]
    public void BuildImportFailureMessage_SourceHttpFailure_ReportsStatusWithoutUrlOrToken()
    {
        var exception = new HttpRequestException(
            $"Response from https://gis.example/arcgis?token={Secret} was 502", null, HttpStatusCode.BadGateway);

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.TransferringFeatures);

        message.Should().StartWith("ARCGIS_SERVICE_ERROR:");
        message.Should().Contain("HTTP 502");
        message.Should().NotContain(Secret).And.NotContain("gis.example");
    }

    [Fact]
    public void BuildImportFailureMessage_ImporterAbort_ReportsTheImportersOwnReason()
    {
        var exception = new GeoservicesImportAbortedException(
            "ArcGIS object-id window 3 exceeded the source transfer limit; the import was not completed.");

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.TransferringFeatures);

        message.Should().Be(
            "ARCGIS_IMPORT_ABORTED: ArcGIS object-id window 3 exceeded the source transfer limit; the import was not completed.");
    }

    [Fact]
    public void BuildImportFailureMessage_UnclassifiedFailure_NamesStageButNotExceptionText()
    {
        var exception = new InvalidOperationException($"Host=db;Password={Secret}");

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.DiscoveringLayer);

        message.Should().StartWith("ARCGIS_IMPORT_FAILED:");
        message.Should().Contain("discovering source layer metadata");
        message.Should().NotContain(Secret);
    }

    [Fact]
    public void BuildImportFailureMessage_ConnectionLostDuringCommit_DoesNotClaimRollbackOrCommit()
    {
        var exception = new NpgsqlException(
            $"Connection lost after sending COMMIT; Password={Secret}", new IOException("Connection reset"));

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.Committing);

        message.Should().StartWith("ARCGIS_IMPORT_DATABASE_UNAVAILABLE:");
        message.Should().Contain("while committing the imported table");
        message.Should().Contain("commit outcome could not be confirmed");
        message.Should().Contain("check the target table and its publication before retrying");
        message.Should().NotContain("No imported data was committed");
        message.Should().NotContain("Imported rows were already committed");
        message.Should().NotContain(Secret).And.NotContain("Connection reset");
    }

    [Fact]
    public void BuildImportFailureMessage_FailureAfterCommit_SaysTheDataWasCommitted()
    {
        var exception = new PostgresException("publish failed", "ERROR", "ERROR", "23505");

        var message = GeoservicesImportService.BuildImportFailureMessage(
            exception, GeoservicesImportService.ImportFailureStage.AfterCommit);

        message.Should().Contain("Imported rows were already committed");
        message.Should().NotContain("No imported data was committed");
    }
}
