// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class GeoServicesRestSourceKindReaderFloorTests
{
    private const string Migration = "144_RenameGeoServicesRestSourceKind.sql";

    [UnitTest]
    public void Migration144_RefusesReadersThatOnlyKnowTheLegacySourceKind()
    {
        var sql = File.ReadAllText(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", Migration));
        var declaration = MigrationReaderCompatibility.Declare(Migration, sql);

        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.ReaderIncompatible);
        MigrationReaderCompatibility.Verify(declaration, Migration, 143).RefusalReason.Should()
            .Be("declared reader-incompatible, not expand");
        File.ReadAllText(RepositoryPaths.Resolve("docs", "guides", "deploy", "upgrade-and-rollback.md"))
            .Should().Contain(Path.GetFileNameWithoutExtension(Migration), "the rollback-boundary note names every reader-incompatible migration");
    }
}
