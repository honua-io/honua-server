// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: AnalyzerBaseline ROOT SARIF_DIRECTORY OUTPUT");
    return 2;
}

var rows = Capture(Path.GetFullPath(args[0]), args[1]);
File.WriteAllText(args[2], "# ID\trepository path\tSHA256(message + LF + enclosing scope + LF + trimmed line)\tcount\n" + rows);
return 0;

static string Capture(string root, string reportsDirectory)
{
    var reports = Directory.GetFiles(reportsDirectory, "*.sarif");
    if (reports.Length == 0)
    {
        throw new InvalidDataException("No SARIF reports found; refusing to replace the baseline");
    }

    var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
    var findings = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var report in reports)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        foreach (var result in Results(document))
        {
            var finding = Finding(root, result, trees);
            findings[finding.Location] = finding.Identity;
        }
    }

    return string.Concat(findings.Values.GroupBy(value => value).OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => group.Key + "\t" + group.Count().ToString(CultureInfo.InvariantCulture) + "\n"));
}

static IEnumerable<JsonElement> Results(JsonDocument document)
{
    foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
    {
        foreach (var result in RunResults(run))
        {
            yield return result;
        }
    }
}

static IEnumerable<JsonElement> RunResults(JsonElement run)
{
    string[] rules = ["CA2000", "IDE0063", "RS0030", "S1244", "S2486", "S108", "CA1508", "CA1851"];
    if (!run.TryGetProperty("results", out var results))
    {
        yield break;
    }

    foreach (var result in results.EnumerateArray())
    {
        if (rules.Contains(result.GetProperty("ruleId").GetString()) &&
            (!result.TryGetProperty("suppressions", out var suppressions) || suppressions.GetArrayLength() == 0))
        {
            yield return result;
        }
    }
}

static (string Location, string Identity) Finding(string root, JsonElement result, Dictionary<string, SyntaxTree> trees)
{
    var physical = result.GetProperty("locations")[0].GetProperty("physicalLocation");
    var uri = physical.GetProperty("artifactLocation").GetProperty("uri").GetString()!;
    var path = Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.LocalPath : Path.GetFullPath(Path.Join(root, Uri.UnescapeDataString(uri)));
    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
    {
        throw new InvalidDataException("Source outside repository: " + path);
    }

    if (!trees.TryGetValue(path, out var tree))
    {
        tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        trees.Add(path, tree);
    }

    var region = physical.GetProperty("region");
    var line = region.GetProperty("startLine").GetInt32() - 1;
    var column = region.TryGetProperty("startColumn", out var startColumn) ? startColumn.GetInt32() - 1 : 0;
    var message = result.GetProperty("message").GetProperty("text").GetString()!;
    var rule = result.GetProperty("ruleId").GetString()!;
    var hash = ReviewFindingIdentity.Fingerprint(tree, tree.GetText().Lines[line].Start + column, message);
    return (rule + "\t" + relative + "\t" + line + "\t" + column + "\t" + message,
        rule + "\t" + relative + "\t" + hash);
}
