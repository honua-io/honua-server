// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.ControlPlane.Restore;

namespace Honua.ControlPlane;

internal readonly record struct ComposeCompatibilityRow(
    string ScriptName,
    int DeclarationFormat,
    string Classification,
    int MinReaderSchema);

// Rollback must restore when a script above the prior reader is missing, out of contract, or reader-incompatible.
internal readonly record struct ComposeRestoreAssessment(bool Required, IReadOnlyList<string> ExpandScriptsToVerify);

internal static class ComposeReaderGate
{
    internal static bool IsSafeScriptName(string scriptName)
        => ComposeExecutorRules.ScriptName().IsMatch(scriptName);

    internal static int? SchemaVersion(IEnumerable<string> scriptNames)
    {
        ArgumentNullException.ThrowIfNull(scriptNames);
        var max = 0;
        foreach (var scriptName in scriptNames)
        {
            var number = MigrationReaderCompatibility.ReadSchemaNumber(scriptName);
            if (number is null)
            {
                return null;
            }

            if (number.Value > max)
            {
                max = number.Value;
            }
        }

        return max;
    }

    internal static IReadOnlyList<AppliedSchemaMigration> Applied(
        IReadOnlyList<string> journal,
        IReadOnlyList<ComposeCompatibilityRow> rows)
        => journal.Select(script =>
        {
            var row = Find(rows, script);
            var number = MigrationReaderCompatibility.ReadSchemaNumber(script);
            return new AppliedSchemaMigration(
                number ?? int.MaxValue,
                number.HasValue && row.HasValue && row.Value.DeclarationFormat == MigrationReaderCompatibilityDeclaration.CurrentFormat ? row.Value.Classification : null,
                row?.MinReaderSchema ?? int.MaxValue);
        }).ToArray();

    internal static ComposeRestoreAssessment Assess(
        IReadOnlyList<string> journal,
        IReadOnlyList<ComposeCompatibilityRow> rows,
        int priorMaxSchema)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(rows);
        var applied = Applied(journal, rows);
        if (applied.Any(migration => !ReaderIncompatibleRestorePolicy.OlderReaderCanAccept(migration, priorMaxSchema)))
        {
            return new ComposeRestoreAssessment(true, []);
        }

        return new ComposeRestoreAssessment(false,
            journal.Where(script => MigrationReaderCompatibility.ReadSchemaNumber(script) > priorMaxSchema).ToArray());
    }

    private static ComposeCompatibilityRow? Find(IReadOnlyList<ComposeCompatibilityRow> rows, string script)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.ScriptName, script, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }
}
