// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Pins REST upload and MCP inline ingest to the same import authorization seam.
/// </summary>
public sealed class ImportAuthorizationArchitectureTests
{
    private const string AuthorizationCall = "ImportAdminAuthorization.IsAuthorizedAsync(";
    private const string ImportCall = "ImportFileAsync(";

    [ArchitectureTest]
    public void ImportTransports_AuthorizeThroughCanonicalSeamBeforeImporting()
    {
        var root = FindRepositoryRoot();
        AssertGuardedAdapter(
            Path.Join(
                root,
                "src",
                "Honua.Import",
                "Features",
                "FileImport",
                "ImportEndpoints.cs"));
        AssertGuardedAdapter(
            Path.Join(
                root,
                "src",
                "Honua.Ai",
                "Features",
                "Protocols",
                "Mcp",
                "Mcp",
                "Tools",
                "IngestDatasetTool.cs"));
    }

    private static void AssertGuardedAdapter(string path)
    {
        var source = File.ReadAllText(path);
        var authorizationIndex = source.IndexOf(AuthorizationCall, StringComparison.Ordinal);
        var importIndex = source.IndexOf(ImportCall, StringComparison.Ordinal);

        Assert.True(
            authorizationIndex >= 0,
            $"{Path.GetFileName(path)} must invoke the canonical import authorization seam.");
        Assert.True(
            importIndex > authorizationIndex,
            $"{Path.GetFileName(path)} must authorize before invoking the import service.");
        Assert.Equal(1, CountOccurrences(source, ImportCall));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
