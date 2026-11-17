// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using DbUp.Engine;
using DbUp.Engine.Transactions;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

// Some tables are always created in the honua schema whatever Database:Schema names: the feature
// change log (the change-tracking trigger writes it there) and the analysis content items. A few
// migrations reach them through $HonuaSchema$, which under a configured schema names a table that
// does not exist. Their hashes are pinned, so those references are bound back to honua here.
internal sealed class HonuaResidentTableScriptProvider(IScriptProvider source) : IScriptProvider
{
    internal const string ResidentSchema = "honua";

    private static readonly IReadOnlyDictionary<string, Regex> _residentReferences = new Dictionary<string, Regex>(StringComparer.Ordinal)
    {
        ["Honua.Server.Migrations.157_FeatureChangeRealtimeStream.sql"] = ReferencesTo("feature_changes", "feature_change_outbox"),
        ["Honua.Server.Migrations.199_AddAnalysisContentTenantOwnership.sql"] = ReferencesTo("analysis_content_items")
    };

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => source.GetScripts(connectionManager).Select(Render);

    internal static SqlScript Render(SqlScript script)
        => _residentReferences.TryGetValue(script.Name, out var references)
            ? new SqlScript(script.Name, references.Replace(script.Contents, ResidentSchema + ".$1"), script.SqlScriptOptions)
            : script;

    private static Regex ReferencesTo(params string[] tables)
        => new(@"\$HonuaSchema\$\.(" + string.Join('|', tables) + @")\b", RegexOptions.CultureInvariant);
}
