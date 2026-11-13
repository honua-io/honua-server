// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Honua.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReviewBaselineSuppressor : DiagnosticSuppressor
{
    private static readonly ImmutableArray<SuppressionDescriptor> Rules =
        ImmutableArray.Create("CA2000", "IDE0063", "RS0030", "S1244", "S2486", "S108", "CA1508", "CA1851")
            .Select((id, index) => new SuppressionDescriptor(
                "HNS" + (index + 1).ToString("0000", CultureInfo.InvariantCulture), id,
                "Existing review finding recorded in scripts/ci/analyzer-baseline/AnalyzerBaseline.tsv"))
            .ToImmutableArray();

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => Rules;

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var baseline = context.Options.AdditionalFiles.FirstOrDefault(
            file => string.Equals(Path.GetFileName(file.Path), "AnalyzerBaseline.tsv", StringComparison.Ordinal));
        var text = baseline?.GetText(context.CancellationToken);
        if (baseline is null || text is null)
        {
            return;
        }

        // The baseline's fixed repository-relative location is also the path root.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(baseline.Path))))!;
        var budgets = ReadBudgets(text.ToString());
        var groups = context.ReportedDiagnostics
            .Where(diagnostic => diagnostic.Location.IsInSource && Rules.Any(rule => rule.SuppressedDiagnosticId == diagnostic.Id))
            .GroupBy(diagnostic => Key(diagnostic, root));
        foreach (var group in groups)
        {
            // Fail every matching occurrence on growth, rather than arbitrarily exempting
            // whichever diagnostic happens to arrive first from concurrent analyzers.
            if (!budgets.TryGetValue(group.Key, out var budget) || group.Count() > budget)
            {
                continue;
            }

            foreach (var diagnostic in group)
            {
                var rule = Rules.First(candidate => candidate.SuppressedDiagnosticId == diagnostic.Id);
                context.ReportSuppression(Suppression.Create(rule, diagnostic));
            }
        }
    }

    private static Dictionary<string, int> ReadBudgets(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length == 4 && int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0)
            {
                var key = fields[0] + "\t" + fields[1] + "\t" + fields[2];
                if (result.ContainsKey(key))
                {
                    return new Dictionary<string, int>(StringComparer.Ordinal);
                }
                result.Add(key, count);
            }
        }
        return result;
    }

    private static string Key(Diagnostic diagnostic, string root)
    {
        var tree = diagnostic.Location.SourceTree!;
        var path = Path.GetFullPath(tree.FilePath).Replace('\\', '/');
        var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var hash = ReviewFindingIdentity.Fingerprint(tree, diagnostic.Location.SourceSpan.Start,
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        return diagnostic.Id + "\t" + path.Substring(prefix.Length) + "\t" + hash;
    }
}
