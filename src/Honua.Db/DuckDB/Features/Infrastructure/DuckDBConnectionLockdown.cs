// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;

namespace Honua.Db.DuckDB.Features.Infrastructure;

// Restricts a DuckDB database instance once bootstrap has loaded its extensions and
// object-store settings: only the configured external-source paths stay readable,
// extensions are never installed or loaded on demand, and settings are locked.
internal static class DuckDBConnectionLockdown
{
    private const string FileScheme = "file://";
    private const string LockSettingSql = "SELECT current_setting('lock_configuration')";
    private static readonly char[] GlobCharacters = ['*', '?', '[', '{'];

    public static async Task<bool> IsLockedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = LockSettingSql;
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return IsTrue(value);
    }

    public static bool IsLocked(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = LockSettingSql;
        return IsTrue(cmd.ExecuteScalar());
    }

    public static async Task<string?> ReadTempDirectoryAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_setting('temp_directory')";
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value?.ToString();
    }

    // Glob paths and directory paths allow their directory prefix; plain file
    // paths allow that file only.
    public static ImmutableArray<DuckDBBootstrapSqlCommand> BuildCommands(
        IEnumerable<string> sourcePaths,
        string? tempDirectory)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var directories = new SortedSet<string>(StringComparer.Ordinal);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            AddSourcePath(path, directories, files);
        }

        if (!string.IsNullOrWhiteSpace(tempDirectory))
        {
            var temp = AsDirectory(tempDirectory);
            directories.Add(temp);
            AddFullLocalPath(temp, directories);
        }

        var commands = ImmutableArray.CreateBuilder<DuckDBBootstrapSqlCommand>();
        commands.Add(new("SET autoinstall_known_extensions = false", "disable DuckDB extension auto-install"));
        commands.Add(new("SET autoload_known_extensions = false", "disable DuckDB extension auto-load"));
        if (directories.Count > 0)
        {
            commands.Add(new("SET allowed_directories = " + ToList(directories), "allow the configured DuckDB source directories"));
        }

        if (files.Count > 0)
        {
            commands.Add(new("SET allowed_paths = " + ToList(files), "allow the configured DuckDB source files"));
        }

        commands.Add(new("SET enable_external_access = false", "disable DuckDB access outside the configured sources"));
        commands.Add(new("SET lock_configuration = true", "lock DuckDB configuration"));
        return commands.ToImmutable();
    }

    private static void AddSourcePath(string path, SortedSet<string> directories, SortedSet<string> files)
    {
        var globIndex = path.IndexOfAny(GlobCharacters);
        if (globIndex < 0 && !EndsWithSeparator(path))
        {
            files.Add(path);
            AddFullLocalPath(path, files);
            return;
        }

        var prefix = globIndex < 0 ? path : path[..globIndex];
        var separator = prefix.LastIndexOfAny(['/', '\\']);
        var directory = separator < 0 ? "./" : prefix[..(separator + 1)];
        directories.Add(directory);
        AddFullLocalPath(directory, directories);
    }

    // Relative and file:// local paths have a second spelling; allow both so the
    // check matches however DuckDB normalizes them.
    private static void AddFullLocalPath(string path, SortedSet<string> target)
    {
        if (path.StartsWith(FileScheme, StringComparison.OrdinalIgnoreCase))
        {
            target.Add(path[FileScheme.Length..]);
            return;
        }

        if (path.Contains("://", StringComparison.Ordinal) || Path.IsPathFullyQualified(path))
        {
            return;
        }

        var full = Path.GetFullPath(path);
        target.Add(EndsWithSeparator(path) ? AsDirectory(full) : full);
    }

    private static bool EndsWithSeparator(string path)
        => path.EndsWith('/') || path.EndsWith('\\');

    private static string AsDirectory(string path)
        => EndsWithSeparator(path) ? path : path + "/";

    private static string ToList(IEnumerable<string> values)
        => "[" + string.Join(", ", values.Select(DuckDBExternalSourceSql.QuoteLiteral)) + "]";

    private static bool IsTrue(object? value)
        => value switch
        {
            bool flag => flag,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false,
        };
}
