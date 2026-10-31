// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

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

    /// <summary>
    /// True when the prior image has no max-schema label and the journal is not entirely expand.
    /// An expand-only journal may still switch images. A contract or reader-incompatible entry may not.
    /// </summary>
    internal static bool LabelMissingBlocks(
        IReadOnlyList<string> journal,
        IReadOnlyList<ComposeCompatibilityRow> rows)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var script in journal)
        {
            if (MigrationReaderCompatibility.ReadSchemaNumber(script) is null)
            {
                return true;
            }

            var row = Find(rows, script);
            if (row is null
                || !string.Equals(row.Value.Classification, MigrationReaderCompatibilityClassification.Expand, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
        var verify = new List<string>();
        foreach (var script in journal)
        {
            var number = MigrationReaderCompatibility.ReadSchemaNumber(script);
            if (number is null || number.Value <= priorMaxSchema)
            {
                continue;
            }

            var row = Find(rows, script);
            if (row is null
                || row.Value.DeclarationFormat != MigrationReaderCompatibilityDeclaration.CurrentFormat
                || !string.Equals(row.Value.Classification, MigrationReaderCompatibilityClassification.Expand, StringComparison.Ordinal)
                || row.Value.MinReaderSchema > priorMaxSchema)
            {
                return new ComposeRestoreAssessment(true, []);
            }

            verify.Add(script);
        }

        return new ComposeRestoreAssessment(false, verify);
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
