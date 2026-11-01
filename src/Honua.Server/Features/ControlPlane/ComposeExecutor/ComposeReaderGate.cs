// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.ControlPlane.Restore;

namespace Honua.ControlPlane;

/// <summary>
/// One compatibility row without its script text. Script text is loaded only for expand rows that
/// still need <see cref="MigrationReaderCompatibility.Verify"/>.
/// </summary>
/// <param name="ScriptName">Journal identity, including a fully qualified DbUp name.</param>
/// <param name="DeclarationFormat">Recorded declaration format.</param>
/// <param name="Classification">Recorded classification.</param>
/// <param name="MinReaderSchema">Oldest reader schema the declaration accepts.</param>
internal readonly record struct ComposeCompatibilityRow(
    string ScriptName,
    int DeclarationFormat,
    string Classification,
    int MinReaderSchema);

/// <summary>Whether rollback must restore, and which expand scripts still need <c>Verify</c>.</summary>
/// <param name="Required">True when a script above the prior reader is missing, contract, or reader-incompatible.</param>
/// <param name="ExpandScriptsToVerify">Expand scripts above the prior reader whose text must be re-verified.</param>
internal readonly record struct ComposeRestoreAssessment(bool Required, IReadOnlyList<string> ExpandScriptsToVerify);

/// <summary>
/// Rollback compatibility using <see cref="MigrationReaderCompatibility"/> identities and
/// classifications. DbUp journal names look like <c>Honua.Server.Migrations.142_Name.sql</c>.
/// </summary>
internal static class ComposeReaderGate
{
    /// <summary>Script names the executor will embed in SQL. Anything else fails closed.</summary>
    internal static bool IsSafeScriptName(string scriptName)
        => ComposeExecutorRules.ScriptName().IsMatch(scriptName);

    /// <summary>
    /// Highest migration number in <paramref name="scriptNames"/>, or 0 when the journal is empty.
    /// Returns null when any name is not a numbered migration identity.
    /// </summary>
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

    /// <summary>Maps database declarations to the shared restore policy inputs.</summary>
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

    /// <summary>
    /// Decides restore from recorded classification and minimum reader schema. Expand scripts above
    /// <paramref name="priorMaxSchema"/> are returned for <see cref="MigrationReaderCompatibility.Verify"/>.
    /// </summary>
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
