// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using DbUp.Engine;
using DbUp.Engine.Transactions;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

// Some tables are always created in the honua schema whatever Database:Schema names: the feature
// change log (the change-tracking trigger writes it there), the analysis content items and the
// RBAC role store. A few migrations reach them through $HonuaSchema$, which under a configured
// schema names a table that does not exist. Their hashes are pinned, so those references are
// bound back to honua here. The caller supplies the script identities.
internal sealed class HonuaResidentTableScriptProvider : IScriptProvider
{
    internal const string ResidentSchema = "honua";

    private readonly IScriptProvider _source;
    private readonly Dictionary<string, Regex> _residentReferences;

    internal HonuaResidentTableScriptProvider(
        IScriptProvider source,
        IEnumerable<HonuaResidentTableBinding> bindings)
    {
        _source = source;
        _residentReferences = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            _residentReferences[binding.ScriptName] = ReferencesTo(binding.Tables);
        }
    }

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
        => _source.GetScripts(connectionManager).Select(Render);

    internal SqlScript Render(SqlScript script)
        => _residentReferences.TryGetValue(script.Name, out var references)
            ? new SqlScript(script.Name, references.Replace(script.Contents, ResidentSchema + ".$1"), script.SqlScriptOptions)
            : script;

    private static Regex ReferencesTo(IReadOnlyList<string> tables)
        => new(@"\$HonuaSchema\$\.(" + string.Join('|', tables) + @")\b", RegexOptions.CultureInvariant);
}

internal sealed class HonuaResidentTableBinding
{
    internal HonuaResidentTableBinding(string scriptName, params string[] tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        if (tables.Length == 0)
        {
            throw new ArgumentException("A resident table binding names at least one table.", nameof(tables));
        }

        ScriptName = scriptName;
        Tables = tables;
    }

    internal string ScriptName { get; }

    internal IReadOnlyList<string> Tables { get; }
}
