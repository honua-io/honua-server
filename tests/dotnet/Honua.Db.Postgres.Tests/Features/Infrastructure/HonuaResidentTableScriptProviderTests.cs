// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DbUp.Engine;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure;

public sealed class HonuaResidentTableScriptProviderTests
{
    [Fact]
    public void Render_ChangeLogMigration_BindsOnlyTheChangeLogToHonua()
    {
        var script = new SqlScript("Honua.Server.Migrations.157_FeatureChangeRealtimeStream.sql", """
            CREATE OR REPLACE TRIGGER t AFTER INSERT ON $HonuaSchema$.feature_changes
                FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.notify_feature_changes();
            CREATE INDEX IF NOT EXISTS ix ON $HonuaSchema$.feature_change_outbox (event_id);
            CREATE TABLE IF NOT EXISTS $HonuaSchema$.feature_changes_archive (id BIGINT);
            CREATE TABLE IF NOT EXISTS $HonuaSchema$.stream_cursors (name TEXT);
            """);

        var rendered = HonuaResidentTableScriptProvider.Render(script).Contents;

        Assert.Contains("AFTER INSERT ON honua.feature_changes", rendered, StringComparison.Ordinal);
        Assert.Contains("ON honua.feature_change_outbox (event_id)", rendered, StringComparison.Ordinal);
        Assert.Contains("$HonuaSchema$.notify_feature_changes()", rendered, StringComparison.Ordinal);
        Assert.Contains("$HonuaSchema$.feature_changes_archive", rendered, StringComparison.Ordinal);
        Assert.Contains("$HonuaSchema$.stream_cursors", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnalysisTenantMigration_KeepsStudioItemsInTheConfiguredSchema()
    {
        var script = new SqlScript("Honua.Server.Migrations.199_AddAnalysisContentTenantOwnership.sql", """
            UPDATE $HonuaSchema$.analysis_content_items AS items
            SET tenant_id = studio.tenant_id
            FROM $HonuaSchema$.studio_content_items AS studio;
            """);

        var rendered = HonuaResidentTableScriptProvider.Render(script).Contents;

        Assert.Contains("UPDATE honua.analysis_content_items AS items", rendered, StringComparison.Ordinal);
        Assert.Contains("FROM $HonuaSchema$.studio_content_items AS studio", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UnlistedScript_IsUnchanged()
    {
        var script = new SqlScript("Honua.Server.Migrations.999_Future.sql",
            "CREATE INDEX IF NOT EXISTS ix ON $HonuaSchema$.feature_changes (event_id);");

        Assert.Same(script, HonuaResidentTableScriptProvider.Render(script));
    }
}
