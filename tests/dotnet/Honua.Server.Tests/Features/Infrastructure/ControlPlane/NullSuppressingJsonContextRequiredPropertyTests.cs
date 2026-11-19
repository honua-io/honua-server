// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// A context that omits nulls on write cannot read back a required property whose value was null:
// the property is missing, so deserialization refuses the whole record or response. Such a
// property must be optional, or written explicitly with JsonIgnore(Condition = Never).
public sealed class NullSuppressingJsonContextRequiredPropertyTests
{
    [UnitTest]
    public void NullSuppressingContexts_DeclareNoRequiredNullableProperty()
    {
        var violations = HonuaAssemblies()
            .SelectMany(LoadableTypes)
            .Where(static type => !type.IsAbstract && type.IsSubclassOf(typeof(JsonSerializerContext)))
            .Select(static type => DefaultInstance(type))
            .Where(static context => context?.Options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull)
            .SelectMany(static context => RequiredNullableProperties(context!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        string.Join(Environment.NewLine, violations).Should().BeEmpty();
    }

    [UnitTest]
    public void ControlPlaneContext_IsScannedDownToTheStoredApproval()
    {
        var context = DefaultInstance(typeof(ControlPlaneJsonContext))!;

        HonuaAssemblies().Should().Contain(typeof(ControlPlaneJsonContext).Assembly);
        context.Options.DefaultIgnoreCondition.Should().Be(JsonIgnoreCondition.WhenWritingNull);
        ReachableTypes(context).Select(static info => info.Type).Should().Contain(typeof(DeployHumanApproval));
    }

    private static IEnumerable<string> RequiredNullableProperties(JsonSerializerContext context)
        => ReachableTypes(context).SelectMany(info => info.Properties
            .Where(IsRequiredAndOmittedWhenNull)
            .Select(property => $"{context.GetType().Name}: {info.Type.FullName}.{property.Name}"));

    private static IEnumerable<JsonTypeInfo> ReachableTypes(JsonSerializerContext context)
    {
        var visited = new HashSet<Type>();
        var pending = new Queue<Type>(RootTypes(context.GetType()));
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type) || TypeInfo(context, type) is not { } info)
            {
                continue;
            }

            foreach (var next in ChildTypes(info))
            {
                pending.Enqueue(next);
            }

            yield return info;
        }
    }

    private static bool IsRequiredAndOmittedWhenNull(JsonPropertyInfo property)
        => property.IsRequired
            && (property.IsSetNullable || Nullable.GetUnderlyingType(property.PropertyType) is not null)
            && (property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
                .OfType<JsonIgnoreAttribute>()
                .Any(static ignore => ignore.Condition == JsonIgnoreCondition.Never) != true);

    private static IEnumerable<Type> ChildTypes(JsonTypeInfo info)
        => info.Properties.Select(static property => property.PropertyType)
            .Append(info.ElementType)
            .Append(info.KeyType)
            .Append(Nullable.GetUnderlyingType(info.Type))
            .Concat(info.PolymorphismOptions?.DerivedTypes.Select(static derived => derived.DerivedType) ?? [])
            .OfType<Type>();

    private static JsonTypeInfo? TypeInfo(JsonSerializerContext context, Type type)
    {
        try
        {
            return context.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<Type> RootTypes(Type contextType)
        => contextType.GetCustomAttributesData()
            .Where(static data => data.AttributeType == typeof(JsonSerializableAttribute))
            .Select(static data => data.ConstructorArguments[0].Value)
            .OfType<Type>();

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static JsonSerializerContext? DefaultInstance(Type contextType)
        => contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as JsonSerializerContext;

    private static IReadOnlyList<Assembly> HonuaAssemblies()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(NullSuppressingJsonContextRequiredPropertyTests).Assembly]);
        while (pending.TryDequeue(out var assembly))
        {
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()
                .Where(static name => name.Name?.StartsWith("Honua.", StringComparison.Ordinal) == true))
            {
                pending.Enqueue(Assembly.Load(reference));
            }
        }

        return [.. found.Values.Where(static assembly => assembly.GetName().Name != typeof(NullSuppressingJsonContextRequiredPropertyTests).Assembly.GetName().Name)];
    }
}
