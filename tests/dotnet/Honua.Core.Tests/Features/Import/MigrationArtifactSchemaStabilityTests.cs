// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Pins the public JSON shape of migration acceptance suite artifacts. When an
/// upstream slice (issues #1015 / #1016 / #1017 / #1018 and friends) adds a
/// field, the developer must regenerate the snapshot in
/// <c>MigrationArtifactSchemaSnapshots/</c> alongside the change so the
/// acceptance-suite contract is updated deliberately.
/// </summary>
public sealed class MigrationArtifactSchemaStabilityTests
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = true
    };

    [Theory]
    [InlineData(typeof(MigrationSourceInventoryArtifact), "honua.migration.source-inventory.schema.json")]
    [InlineData(typeof(MigrationManifestArtifact), "honua.migration.manifest.schema.json")]
    [InlineData(typeof(MigrationParityEvidenceArtifact), "honua.migration.parity-evidence-pack.schema.json")]
    [InlineData(typeof(MigrationCutoverReadinessAttestationArtifact), "honua.migration.cutover-readiness-attestation.schema.json")]
    public void Artifact_Schema_Matches_GoldenSnapshot(Type artifactType, string snapshotFileName)
    {
        var schema = MigrationArtifactSchemaSnapshot.Capture(artifactType);
        var actual = JsonSerializer.Serialize(schema, SnapshotJsonOptions);

        var snapshotPath = ResolveSnapshotPath(snapshotFileName);

        if (Environment.GetEnvironmentVariable("HONUA_TEST_UPDATE_SNAPSHOTS") == "1")
        {
            var sourceSnapshotPath = ResolveSourceSnapshotPath(snapshotFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceSnapshotPath)!);
            File.WriteAllText(sourceSnapshotPath, actual + "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, actual + "\n");
        }

        File.Exists(snapshotPath).Should().BeTrue(
            $"snapshot file '{snapshotPath}' should exist. " +
            "To accept a deliberate artifact shape change, regenerate it by " +
            "rerunning the test with HONUA_TEST_UPDATE_SNAPSHOTS=1 and committing " +
            "the updated golden file.");

        var expected = File.ReadAllText(snapshotPath);
        NormalizeForCompare(actual).Should().Be(
            NormalizeForCompare(expected),
            $"the JSON shape of {artifactType.Name} must remain stable. " +
            $"If this change is intentional, update '{snapshotFileName}' and " +
            "co-ordinate the migration acceptance workflow consumers.");
    }

    private static string ResolveSnapshotPath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(
            baseDir,
            "Features",
            "Import",
            "MigrationArtifactSchemaSnapshots",
            fileName);
    }

    private static string ResolveSourceSnapshotPath(string fileName)
    {
        // Walk up from the test assembly output (bin/<config>/<tfm>) to the
        // project root so the regenerated snapshot lands in source control.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && current.Name != "Honua.Core.Tests")
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new InvalidOperationException(
                "Unable to locate the Honua.Core.Tests project directory from the test runtime.");
        }

        return Path.Combine(
            current.FullName,
            "Features",
            "Import",
            "MigrationArtifactSchemaSnapshots",
            fileName);
    }

    private static string NormalizeForCompare(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}

/// <summary>
/// Walks the property graph of a migration artifact record and emits a
/// deterministic shape representation used by the schema stability tests.
/// </summary>
internal static class MigrationArtifactSchemaSnapshot
{
    public static SchemaNode Capture(Type artifactType) => CaptureType(artifactType, new HashSet<Type>());

    private static SchemaNode CaptureType(Type type, HashSet<Type> visiting)
    {
        var node = new SchemaNode
        {
            TypeName = FormatTypeName(type)
        };

        if (IsRecordObject(type) && visiting.Add(type))
        {
            try
            {
                foreach (var property in EnumerateProperties(type))
                {
                    node.Properties ??= [];
                    node.Properties.Add(BuildProperty(property, visiting));
                }
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        return node;
    }

    private static SchemaProperty BuildProperty(PropertyInfo property, HashSet<Type> visiting)
    {
        var propertyType = property.PropertyType;
        var isNullable = IsNullable(property);
        var underlying = Nullable.GetUnderlyingType(propertyType);
        if (underlying is not null)
        {
            propertyType = underlying;
            isNullable = true;
        }

        var (elementType, container) = DescribeContainer(propertyType);
        var shape = new SchemaProperty
        {
            JsonName = ResolveJsonName(property),
            ClrName = property.Name,
            Container = container,
            ElementType = FormatTypeName(elementType),
            Required = IsRequired(property),
            Nullable = isNullable
        };

        if (IsRecordObject(elementType))
        {
            shape.Schema = CaptureType(elementType, visiting);
        }

        return shape;
    }

    private static IEnumerable<PropertyInfo> EnumerateProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static (Type ElementType, string Container) DescribeContainer(Type type)
    {
        if (type == typeof(string))
        {
            return (type, "scalar");
        }

        if (type.IsArray)
        {
            return (type.GetElementType()!, "array");
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(List<>) ||
                def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(ICollection<>))
            {
                return (type.GetGenericArguments()[0], "array");
            }

            if (def == typeof(Dictionary<,>) || def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>))
            {
                return (type.GetGenericArguments()[1], "map");
            }
        }

        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            return (typeof(object), "array");
        }

        return (type, "scalar");
    }

    private static bool IsRecordObject(Type type)
    {
        if (!type.IsClass || type == typeof(string))
        {
            return false;
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        // Records emit a compiler-generated Clone method (<Clone>$).
        return type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;
    }

    private static bool IsRequired(PropertyInfo property) =>
        property.GetCustomAttribute<RequiredMemberAttribute>() is not null;

    private static bool IsNullable(PropertyInfo property)
    {
        var context = new NullabilityInfoContext();
        var info = context.Create(property);
        return info.ReadState == NullabilityState.Nullable;
    }

    private static string ResolveJsonName(PropertyInfo property)
    {
        var explicitName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    private static string FormatTypeName(Type type)
    {
        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(int) || type == typeof(int?))
        {
            return "int";
        }

        if (type == typeof(long) || type == typeof(long?))
        {
            return "long";
        }

        if (type == typeof(bool) || type == typeof(bool?))
        {
            return "bool";
        }

        if (type == typeof(double) || type == typeof(double?))
        {
            return "double";
        }

        if (type == typeof(decimal) || type == typeof(decimal?))
        {
            return "decimal";
        }

        if (type == typeof(DateTime) || type == typeof(DateTime?))
        {
            return "DateTime";
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?))
        {
            return "DateTimeOffset";
        }

        if (type == typeof(object))
        {
            return "object";
        }

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return FormatTypeName(underlying);
        }

        return type.Name;
    }
}

internal sealed class SchemaNode
{
    [JsonPropertyOrder(0)]
    public string TypeName { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SchemaProperty>? Properties { get; set; }
}

internal sealed class SchemaProperty
{
    [JsonPropertyOrder(0)]
    public string JsonName { get; set; } = string.Empty;

    [JsonPropertyOrder(1)]
    public string ClrName { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string ElementType { get; set; } = string.Empty;

    [JsonPropertyOrder(4)]
    public bool Required { get; set; }

    [JsonPropertyOrder(5)]
    public bool Nullable { get; set; }

    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaNode? Schema { get; set; }
}
