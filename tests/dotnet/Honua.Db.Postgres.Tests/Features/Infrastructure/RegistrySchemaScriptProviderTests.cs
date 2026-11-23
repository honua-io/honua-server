// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp.Engine;
using DbUp.Engine.Transactions;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

public sealed class RegistrySchemaScriptProviderTests
{
    [Fact]
    public void Render_FrozenRasterMigration_PreservesContractReviewAfterBinding()
    {
        var script = ReadRasterMigration();
        Assert.Equal(MigrationSafetyClassification.ContractAnnotated, Classify(script));

        var rendered = new RegistrySchemaScriptProvider(Unused.Instance, "custom_registry").Render(script);

        Assert.Contains("REFERENCES \"custom_registry\".layers(layer_id)", rendered.Contents, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES honua.layers(layer_id)", rendered.Contents, StringComparison.Ordinal);
        Assert.Equal(MigrationSafetyClassification.ContractAnnotated, Classify(rendered));
    }

    [Fact]
    public void Render_AlteredRasterMigration_DoesNotGrantReviewToUnrecognizedBytes()
    {
        var original = ReadRasterMigration();
        var script = new SqlScript(original.Name, original.Contents + "\nDROP TABLE honua.services;");

        var rendered = new RegistrySchemaScriptProvider(Unused.Instance, "custom_registry").Render(script);

        Assert.Equal(MigrationSafetyClassification.ContractUnannotated, Classify(rendered));
    }

    private static MigrationSafetyClassification Classify(SqlScript script)
        => MigrationSafetyClassifier.Classify(script.Name, script.Contents).Classification;

    private static SqlScript ReadRasterMigration()
    {
        using var stream = typeof(PostgresDatabaseMigrationRunner).Assembly
            .GetManifestResourceStream(PostgresCoreSchemaGuard.RasterTablesMigration)!;
        using var reader = new StreamReader(stream);
        return new SqlScript(PostgresCoreSchemaGuard.RasterTablesMigration, reader.ReadToEnd());
    }

    private sealed class Unused : IScriptProvider
    {
        internal static readonly Unused Instance = new();

        public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
            => throw new NotSupportedException();
    }
}
