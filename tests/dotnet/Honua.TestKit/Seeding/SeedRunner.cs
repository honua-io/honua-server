// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Honua.TestKit.Seeding;

/// <summary>
/// Extension methods for applying shared YAML seed data in tests.
/// </summary>
public static class SeedExtensions
{
    /// <summary>
    /// Apply a YAML seed file to a schema.
    /// </summary>
    /// <param name="fixture">PostgreSQL fixture</param>
    /// <param name="seedPath">Path to the YAML seed file</param>
    /// <param name="schemaName">Schema to apply the seed to</param>
    /// <param name="profile">Optional profile name to filter collections</param>
    public static Task ApplySeedAsync(
        this PostgresFixture fixture,
        string seedPath,
        string? schemaName = null,
        string? profile = null)
    {
        return SeedRunner.ApplyAsync(fixture.DataSource, seedPath, schemaName, profile);
    }

    /// <summary>
    /// Create a schema and apply a YAML seed file to it.
    /// </summary>
    /// <param name="fixture">PostgreSQL fixture</param>
    /// <param name="testClassName">Test class name used for schema naming</param>
    /// <param name="seedPath">Path to the YAML seed file</param>
    /// <param name="profile">Optional profile name to filter collections</param>
    public static async Task<string> CreateSeededSchemaAsync(
        this PostgresFixture fixture,
        string testClassName,
        string seedPath,
        string? profile = null)
    {
        var schema = await fixture.CreateIsolatedSchemaInternalAsync(testClassName, applySeed: false)
            .ConfigureAwait(false);
        await fixture.ApplySeedAsync(seedPath, schema, profile).ConfigureAwait(false);
        return schema;
    }
}

internal static class SeedRunner
{
    private static readonly Regex _identifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex _typeRegex = new("^[A-Za-z0-9_(),\\s\\[\\]]+$", RegexOptions.CultureInvariant);
    private const int MaxConcurrencyRetryAttempts = 3;
    private const long SeedApplicationLockKey = 7_893_641_160_553_452_791;

    // honua-server#1359: the serially-seeded Database collections pay a per-test seed
    // cost. Re-reading and re-parsing the seed YAML from disk on every isolated-schema
    // creation is pure, deterministic work whose result (an init-only SeedDefinition) is
    // immutable once loaded, so memoize it keyed by absolute path + last-write time.
    // This removes a redundant file read + YAML deserialize from every test's seed.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Ticks), SeedDefinition> _seedCache = new();

    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        string seedPath,
        string? schemaName,
        string? profileName)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await ApplyOnceAsync(dataSource, seedPath, schemaName, profileName).ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex) when (IsConcurrencyFailure(ex) && attempt < MaxConcurrencyRetryAttempts)
            {
                await Task.Delay(GetRetryDelay(attempt)).ConfigureAwait(false);
            }
        }
    }

    // Returns the collection names visible to the given profile, or null when the
    // seed applies every collection (no profile filter). Callers can treat a non-null
    // result as the strict allow-list for that profile and validate that declared
    // scenario inputs are a subset before seeding a schema.
    internal static IReadOnlyCollection<string>? GetAllowedCollections(string seedPath, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(seedPath))
        {
            throw new ArgumentException("Seed path is required.", nameof(seedPath));
        }

        if (!File.Exists(seedPath))
        {
            throw new FileNotFoundException($"Seed file not found: {seedPath}", seedPath);
        }

        var seed = LoadSeed(seedPath);
        var profile = ResolveProfile(seed, profileName);
        return ResolveAllowedCollections(profile);
    }

    private static async Task ApplyOnceAsync(
        NpgsqlDataSource dataSource,
        string seedPath,
        string? schemaName,
        string? profileName)
    {
        if (string.IsNullOrWhiteSpace(seedPath))
        {
            throw new ArgumentException("Seed path is required.", nameof(seedPath));
        }

        if (!File.Exists(seedPath))
        {
            throw new FileNotFoundException($"Seed file not found: {seedPath}", seedPath);
        }

        var seed = LoadSeed(seedPath);
        var profile = ResolveProfile(seed, profileName);
        var allowedCollections = ResolveAllowedCollections(profile);
        var collectionLookup = BuildCollectionLookup(seed.Collections);

        await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
        // ReadCommitted (not RepeatableRead): the advisory lock below already serializes
        // seed application. Under parallel collection runs, a waiting RepeatableRead
        // transaction pins its snapshot before the lock is granted and can fail with
        // 40001 once the prior seeder commits its deletes. ReadCommitted re-snapshots
        // per statement while the lock preserves deterministic setup.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        await AcquireSeedApplicationLockAsync(connection).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            await SetSearchPathAsync(connection, schemaName!).ConfigureAwait(false);
        }

        var selectedCollections = FilterCollections(seed.Collections, allowedCollections);
        foreach (var collection in selectedCollections)
        {
            await CreateCollectionAsync(connection, seed, collection).ConfigureAwait(false);
        }

        foreach (var sql in EnumerateSql(seed, profile))
        {
            await ExecuteSqlAsync(connection, sql).ConfigureAwait(false);
        }

        foreach (var feature in seed.Features)
        {
            if (!collectionLookup.TryGetValue(feature.Collection, out var collection))
            {
                throw new InvalidOperationException($"Feature references unknown collection '{feature.Collection}'.");
            }

            if (allowedCollections is not null &&
                !allowedCollections.Contains(collection.Name))
            {
                continue;
            }

            await InsertFeatureAsync(connection, seed, collection, feature).ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static bool IsConcurrencyFailure(PostgresException ex)
        => ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private static TimeSpan GetRetryDelay(int attempt)
        => TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt - 1));

    private static async Task AcquireSeedApplicationLockAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_xact_lock(@lock_key);";
        _ = command.Parameters.AddWithValue("lock_key", SeedApplicationLockKey);
        _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static SeedDefinition LoadSeed(string seedPath)
    {
        // Memoize the parsed seed keyed by absolute path + last-write time so a touched
        // seed file is still picked up, but the common case (same unchanging seed reused
        // across thousands of per-test schema creations) parses exactly once.
        var fullPath = Path.GetFullPath(seedPath);
        var ticks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        return _seedCache.GetOrAdd((fullPath, ticks), static key => ParseSeed(key.Path));
    }

    private static SeedDefinition ParseSeed(string seedPath)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var yaml = File.ReadAllText(seedPath);
        var seed = deserializer.Deserialize<SeedDefinition>(yaml);
        return seed ?? new SeedDefinition();
    }

    private static SeedProfile? ResolveProfile(SeedDefinition seed, string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        if (seed.Profiles is null || !seed.Profiles.TryGetValue(profileName, out var profile))
        {
            throw new InvalidOperationException($"Seed profile '{profileName}' not found.");
        }

        return profile;
    }

    private static HashSet<string>? ResolveAllowedCollections(SeedProfile? profile)
    {
        if (profile?.Collections is null || profile.Collections.Count == 0)
        {
            return null;
        }

        return profile.Collections.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, SeedCollection> BuildCollectionLookup(IEnumerable<SeedCollection> collections)
    {
        var lookup = new Dictionary<string, SeedCollection>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in collections)
        {
            if (string.IsNullOrWhiteSpace(collection.Name))
            {
                throw new InvalidOperationException("Seed collections must have a name.");
            }

            lookup[collection.Name] = collection;
        }

        return lookup;
    }

    private static IEnumerable<SeedCollection> FilterCollections(
        IEnumerable<SeedCollection> collections,
        HashSet<string>? allowedCollections)
    {
        if (allowedCollections is null)
        {
            return collections;
        }

        return collections.Where(c => allowedCollections.Contains(c.Name));
    }

    private static async Task SetSearchPathAsync(NpgsqlConnection connection, string schemaName)
    {
        ValidateIdentifier(schemaName, "schema");
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO {schemaName}, public;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task CreateCollectionAsync(
        NpgsqlConnection connection,
        SeedDefinition seed,
        SeedCollection collection)
    {
        var tableName = ValidateIdentifier(collection.Table ?? collection.Name, "table");
        var geometryColumn = ValidateIdentifier(collection.GeometryColumn ?? "geom", "geometry column");
        var geometryType = ValidateGeometryType(collection.GeometryType ?? "GEOMETRY");
        var srid = collection.Srid ?? seed.Srid ?? 4326;
        var isGeography = collection.Geography;

        var columns = new List<string>
        {
            BuildIdColumn(collection)
        };

        if (collection.Properties is not null)
        {
            foreach (var (name, type) in collection.Properties)
            {
                var columnName = ValidateIdentifier(name, "property");
                var columnType = ValidateType(type, $"property type '{name}'");
                columns.Add($"{columnName} {columnType}");
            }
        }

        var geometryTypeSql = isGeography ? "geography" : "geometry";
        columns.Add($"{geometryColumn} {geometryTypeSql}({geometryType}, {srid})");

        var indexName = BuildIndexName(tableName, geometryColumn);

        var sql = $"""
            CREATE TABLE IF NOT EXISTS {tableName} (
                {string.Join(",\n    ", columns)}
            );
            CREATE INDEX IF NOT EXISTS {indexName}
                ON {tableName} USING GIST ({geometryColumn});
            """;

        await ExecuteSqlAsync(connection, sql).ConfigureAwait(false);
    }

    private static string BuildIdColumn(SeedCollection collection)
    {
        var idColumn = ValidateIdentifier(collection.IdColumn ?? "id", "id column");
        if (string.IsNullOrWhiteSpace(collection.IdType))
        {
            return $"{idColumn} SERIAL PRIMARY KEY";
        }

        var idType = ValidateType(collection.IdType, "id type");
        if (idType.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
        {
            return $"{idColumn} {idType}";
        }

        return $"{idColumn} {idType} PRIMARY KEY";
    }

    private static async Task InsertFeatureAsync(
        NpgsqlConnection connection,
        SeedDefinition seed,
        SeedCollection collection,
        SeedFeature feature)
    {
        var tableName = ValidateIdentifier(collection.Table ?? collection.Name, "table");
        var geometryColumn = ValidateIdentifier(collection.GeometryColumn ?? "geom", "geometry column");
        var idColumn = ValidateIdentifier(collection.IdColumn ?? "id", "id column");
        var srid = collection.Srid ?? seed.Srid ?? 4326;
        var isGeography = collection.Geography;

        var columns = new List<string>();
        var values = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        void AddParameter(string name, object? value)
        {
            var parameter = new NpgsqlParameter(name, value ?? DBNull.Value);
            parameters.Add(parameter);
        }

        string NextParam() => $"p{parameters.Count}";

        if (feature.Id is not null)
        {
            columns.Add(idColumn);
            var paramName = NextParam();
            values.Add($"@{paramName}");
            AddParameter(paramName, NormalizeValueForColumn(feature.Id, collection.IdType ?? "serial"));
        }

        if (feature.Properties is not null)
        {
            foreach (var (name, value) in feature.Properties)
            {
                if (collection.Properties is null ||
                    !collection.Properties.TryGetValue(name, out var propertyType))
                {
                    throw new InvalidOperationException($"Feature property '{name}' not defined on collection '{collection.Name}'.");
                }

                var columnName = ValidateIdentifier(name, "property");
                var paramName = NextParam();
                columns.Add(columnName);
                values.Add($"@{paramName}");
                AddParameter(paramName, NormalizeValueForColumn(value, propertyType));
            }
        }

        if (feature.Geometry is not null)
        {
            columns.Add(geometryColumn);
            values.Add(BuildGeometryExpression(feature.Geometry, srid, isGeography, parameters));
        }

        await using var command = connection.CreateCommand();
        if (columns.Count == 0)
        {
            command.CommandText = $"INSERT INTO {tableName} DEFAULT VALUES;";
        }
        else
        {
            command.CommandText = $"""
                INSERT INTO {tableName} ({string.Join(", ", columns)})
                VALUES ({string.Join(", ", values)});
                """;
        }

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string BuildGeometryExpression(
        SeedGeometry geometry,
        int srid,
        bool isGeography,
        List<NpgsqlParameter> parameters)
    {
        if (!string.IsNullOrWhiteSpace(geometry.Wkt))
        {
            var geomParam = $"p{parameters.Count}";
            parameters.Add(new NpgsqlParameter(geomParam, geometry.Wkt));
            var sridParam = $"p{parameters.Count}";
            parameters.Add(new NpgsqlParameter(sridParam, srid));
            return CastGeography($"ST_SetSRID(ST_GeomFromText(@{geomParam}), @{sridParam})", isGeography);
        }

        if (geometry.Geojson is not null)
        {
            var geoJson = JsonSerializer.Serialize(NormalizeYamlObject(geometry.Geojson));
            var geomParam = $"p{parameters.Count}";
            parameters.Add(new NpgsqlParameter(geomParam, geoJson));
            var sridParam = $"p{parameters.Count}";
            parameters.Add(new NpgsqlParameter(sridParam, srid));
            return CastGeography($"ST_SetSRID(ST_GeomFromGeoJSON(@{geomParam}), @{sridParam})", isGeography);
        }

        return "NULL";
    }

    private static string CastGeography(string expression, bool isGeography)
    {
        return isGeography ? $"{expression}::geography" : expression;
    }

    private static IEnumerable<string> EnumerateSql(SeedDefinition seed, SeedProfile? profile)
    {
        if (seed.Sql is not null)
        {
            foreach (var statement in seed.Sql)
            {
                if (!string.IsNullOrWhiteSpace(statement))
                {
                    yield return statement;
                }
            }
        }

        if (profile?.Sql is not null)
        {
            foreach (var statement in profile.Sql)
            {
                if (!string.IsNullOrWhiteSpace(statement))
                {
                    yield return statement;
                }
            }
        }
    }

    private static async Task ExecuteSqlAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string ValidateIdentifier(string name, string label)
    {
        if (string.IsNullOrWhiteSpace(name) || !_identifierRegex.IsMatch(name))
        {
            throw new InvalidOperationException($"Invalid {label} name '{name}'.");
        }

        return name;
    }

    private static string ValidateGeometryType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "GEOMETRY";
        }

        var normalized = type.Trim().ToUpperInvariant();
        if (!_identifierRegex.IsMatch(normalized))
        {
            throw new InvalidOperationException($"Invalid geometry type '{type}'.");
        }

        return normalized;
    }

    private static string ValidateType(string type, string label)
    {
        if (string.IsNullOrWhiteSpace(type) || !_typeRegex.IsMatch(type))
        {
            throw new InvalidOperationException($"Invalid {label} '{type}'.");
        }

        return type.Trim();
    }

    private static string BuildIndexName(string tableName, string geometryColumn)
    {
        var indexName = $"idx_{tableName}_{geometryColumn}";
        if (indexName.Length <= 60)
        {
            return indexName;
        }

        return indexName[..60];
    }

    private static object? NormalizeYamlObject(object? value)
    {
        return value switch
        {
            null => null,
            IDictionary<string, object?> map => map.ToDictionary(
                entry => entry.Key,
                entry => NormalizeYamlObject(entry.Value)),
            IDictionary<object, object> map => map.ToDictionary(
                entry => entry.Key.ToString() ?? string.Empty,
                entry => NormalizeYamlObject(entry.Value)),
            IEnumerable<object> list => list.Select(NormalizeYamlObject).ToList(),
            string s => s,
            bool b => b,
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal m => m,
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static object? NormalizeValueForColumn(object? value, string declaredType)
    {
        var normalized = NormalizeYamlObject(value);
        if (normalized is not string text)
        {
            return normalized;
        }

        var type = declaredType.Trim().ToLowerInvariant();
        if (IsIntegerType(type) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue))
        {
            return UsesInt64(type)
                ? integerValue
                : checked((int)integerValue);
        }

        if (IsBooleanType(type) &&
            bool.TryParse(text, out var boolValue))
        {
            return boolValue;
        }

        if (IsFloatingPointType(type) &&
            double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        if (IsDecimalType(type) &&
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture,
                out var decimalValue))
        {
            return decimalValue;
        }

        return normalized;
    }

    private static bool IsIntegerType(string declaredType)
        => declaredType.StartsWith("smallint", StringComparison.Ordinal) ||
           declaredType.StartsWith("integer", StringComparison.Ordinal) ||
           declaredType.StartsWith("bigint", StringComparison.Ordinal) ||
           declaredType.StartsWith("int2", StringComparison.Ordinal) ||
           declaredType.StartsWith("int4", StringComparison.Ordinal) ||
           declaredType.StartsWith("int8", StringComparison.Ordinal) ||
           declaredType.Equals("int", StringComparison.Ordinal) ||
           declaredType.StartsWith("int[", StringComparison.Ordinal) ||
           declaredType.StartsWith("serial", StringComparison.Ordinal) ||
           declaredType.StartsWith("bigserial", StringComparison.Ordinal);

    private static bool UsesInt64(string declaredType)
        => declaredType.StartsWith("bigint", StringComparison.Ordinal) ||
           declaredType.StartsWith("bigserial", StringComparison.Ordinal) ||
           declaredType.StartsWith("int8", StringComparison.Ordinal);

    private static bool IsBooleanType(string declaredType)
        => declaredType.StartsWith("bool", StringComparison.Ordinal) ||
           declaredType.StartsWith("boolean", StringComparison.Ordinal);

    private static bool IsFloatingPointType(string declaredType)
        => declaredType.StartsWith("real", StringComparison.Ordinal) ||
           declaredType.StartsWith("double precision", StringComparison.Ordinal) ||
           declaredType.StartsWith("float", StringComparison.Ordinal);

    private static bool IsDecimalType(string declaredType)
        => declaredType.StartsWith("numeric", StringComparison.Ordinal) ||
           declaredType.StartsWith("decimal", StringComparison.Ordinal);
}

internal sealed class SeedDefinition
{
    public int Version { get; init; } = 1;
    public int? Srid { get; init; }
    public Dictionary<string, SeedProfile>? Profiles { get; init; }
    public List<SeedCollection> Collections { get; init; } = [];
    public List<SeedFeature> Features { get; init; } = [];
    public List<string>? Sql { get; init; }
}

internal sealed class SeedProfile
{
    public List<string>? Collections { get; init; }
    public List<string>? Sql { get; init; }
}

internal sealed class SeedCollection
{
    public string Name { get; init; } = string.Empty;
    public string? Table { get; init; }
    public string? GeometryColumn { get; init; }
    public string? GeometryType { get; init; }
    public bool Geography { get; init; }
    public int? Srid { get; init; }
    public string? IdColumn { get; init; }
    public string? IdType { get; init; }
    public Dictionary<string, string>? Properties { get; init; }
}

internal sealed class SeedFeature
{
    public string Collection { get; init; } = string.Empty;
    public object? Id { get; init; }
    public SeedGeometry? Geometry { get; init; }
    public Dictionary<string, object?>? Properties { get; init; }
}

internal sealed class SeedGeometry
{
    public string? Wkt { get; init; }
    public Dictionary<string, object?>? Geojson { get; init; }
}
