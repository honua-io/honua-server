// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure;

public class PostgresFixtureMigrationDiscoveryTests
{
    [UnitTest]
    public void Discover_EmbeddedMarkers_SelectsCanonicalSqlInFixturePhaseOrder()
    {
        var migrations = PostgresFixtureMigrations.Discover();

        migrations.Should().OnlyHaveUniqueItems();
        migrations.Should().Contain("Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql");
        migrations.Should().Contain("Honua.Server.Migrations.167_CreateWorkflowStores.sql");
        migrations.Should().Contain("Honua.Server.Migrations.193_AllowIncidentInvestigationReferences.sql");
        var observabilityStore = Array.IndexOf(migrations, "Honua.Server.Migrations.034_CreateOperateObservability.sql");
        var alertStore = Array.IndexOf(migrations, "Honua.Server.Migrations.013_AddSpatialAlerts.sql");
        var alertChannels = Array.IndexOf(migrations, "Honua.Server.Migrations.014_AddCloudAlertChannels.sql");
        var alertSource = Array.IndexOf(migrations, "Honua.Server.Migrations.075_AddAlertOpsSource.sql");
        var auditStore = Array.IndexOf(migrations, "Honua.Server.Migrations.033_CreateAuditLog.sql");
        var incidentReferences = Array.IndexOf(migrations, "Honua.Server.Migrations.193_AllowIncidentInvestigationReferences.sql");
        alertStore.Should().BeGreaterThanOrEqualTo(0);
        alertChannels.Should().BeGreaterThan(alertStore);
        alertSource.Should().BeGreaterThan(alertChannels);
        auditStore.Should().BeGreaterThanOrEqualTo(0);
        observabilityStore.Should().BeGreaterThan(alertStore).And.BeGreaterThan(auditStore);
        incidentReferences.Should().BeGreaterThan(observabilityStore).And.BeGreaterThan(alertSource);
        var adminKeyExpansion = Array.IndexOf(migrations, "Honua.Server.Migrations.200_ExpandAdminApiKeyStorage.sql");
        var adminKeyStore = Array.IndexOf(migrations, "Honua.Server.Migrations.136_CreateAdminApiKeys.sql");
        var adminKeyRestrictions = Array.IndexOf(migrations, "Honua.Server.Migrations.139_AddAdminApiKeyRestrictions.sql");
        var adminKeyExpiry = Array.IndexOf(migrations, "Honua.Server.Migrations.152_IndexAdminApiKeyExpiry.sql");
        adminKeyStore.Should().BeGreaterThanOrEqualTo(0);
        adminKeyRestrictions.Should().BeGreaterThan(adminKeyStore);
        adminKeyExpiry.Should().BeGreaterThan(adminKeyRestrictions);
        adminKeyExpansion.Should().BeGreaterThan(adminKeyExpiry);
        var lastStore = Array.IndexOf(migrations, "Honua.Server.Migrations.167_CreateWorkflowStores.sql");
        var firstImportFunction = Array.IndexOf(migrations, "Honua.Server.Migrations.070_AddImportLoadModes.sql");
        var typedColumns = Array.IndexOf(migrations, "Honua.Server.Migrations.143_PromoteImportTypedColumns.sql");
        firstImportFunction.Should().BeGreaterThan(lastStore);
        typedColumns.Should().BeGreaterThan(firstImportFunction);

        var assembly = typeof(PostgresFixture).Assembly;
        foreach (var migration in migrations)
        {
            using var stream = assembly.GetManifestResourceStream(migration);
            stream.Should().NotBeNull();
            using var reader = new StreamReader(stream!);
            reader.ReadToEnd().Should().NotBeNullOrWhiteSpace();
        }
    }
}
