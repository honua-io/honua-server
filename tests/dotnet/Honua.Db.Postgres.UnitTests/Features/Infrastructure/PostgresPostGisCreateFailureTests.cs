// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

[Trait("Tier", "Fast")]
public sealed class PostgresPostGisCreateFailureTests
{
    [Fact]
    public void CreateFailure_WhenTheExtensionAppearsAfterTheError_CountsAsCreatedByPeer()
    {
        var duplicate = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");

        PostgresDatabaseCompatibilityChecker
            .DecidePostGisCreateFailure(duplicate, installedAfterFailure: true)
            .Should().Be(PostgresDatabaseCompatibilityChecker.PostGisCreateFailureDisposition.CreatedByPeer);
    }

    [Fact]
    public void CreateFailure_WhenThePackageOrPrivilegeIsMissing_StaysAMissingExtension()
    {
        var privilege = new PostgresException("permission denied to create extension", "ERROR", "ERROR", "42501");
        var missingPackage = new PostgresException("could not open extension control file", "ERROR", "ERROR", "58P01");

        PostgresDatabaseCompatibilityChecker
            .DecidePostGisCreateFailure(privilege, installedAfterFailure: false)
            .Should().Be(PostgresDatabaseCompatibilityChecker.PostGisCreateFailureDisposition.MissingExtension);
        PostgresDatabaseCompatibilityChecker
            .DecidePostGisCreateFailure(missingPackage, installedAfterFailure: false)
            .Should().Be(PostgresDatabaseCompatibilityChecker.PostGisCreateFailureDisposition.MissingExtension);
    }

    [Fact]
    public void CreateFailure_WhenTheCommandTimesOutOrDisconnects_ReachesThePreflightClassifier()
    {
        var timeout = new TimeoutException("Timeout during reading attempt");
        var disconnect = new SocketException((int)SocketError.ConnectionReset);

        PostgresDatabaseCompatibilityChecker
            .DecidePostGisCreateFailure(timeout, installedAfterFailure: false)
            .Should().Be(PostgresDatabaseCompatibilityChecker.PostGisCreateFailureDisposition.Propagate);
        PostgresDatabaseCompatibilityChecker
            .DecidePostGisCreateFailure(disconnect, installedAfterFailure: false)
            .Should().Be(PostgresDatabaseCompatibilityChecker.PostGisCreateFailureDisposition.Propagate);

        var timeoutClass = PostgresPreflightFailureClassifier.Classify(timeout);
        var disconnectClass = PostgresPreflightFailureClassifier.Classify(disconnect);
        timeoutClass.Should().Be(DatabasePreflightCauseClasses.ConnectionUnavailable);
        disconnectClass.Should().Be(DatabasePreflightCauseClasses.ConnectionUnavailable);
        DatabasePreflightCauseClasses.IsDegradable(timeoutClass).Should().BeTrue();
        DatabasePreflightCauseClasses.IsDegradable(disconnectClass).Should().BeTrue();
    }

    [Fact]
    public void CreateFailure_WhenTheRequeryCannotRun_RethrowsTheOriginalFailure()
    {
        var timeout = new TimeoutException("Timeout during reading attempt");

        var act = () => PostgresDatabaseCompatibilityChecker.ThrowPreservedCreateFailure(timeout);

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(timeout);
    }
}
