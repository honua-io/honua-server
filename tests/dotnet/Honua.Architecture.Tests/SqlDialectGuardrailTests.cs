// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guardrails the <see cref="ISqlDialect"/> / <see cref="SqlFilterExpressionVisitorBase"/>
/// contracts introduced for audit C4 (no SQL-dialect abstraction below the AST — identifier
/// quoting reimplemented per-provider, SQL-injection boundary risk).
/// </summary>
/// <remarks>
/// The asserts are deliberately tied to the shipped data providers (Postgres, MySql, SqlServer,
/// DuckDB, Oracle). When another provider is added, extend <see cref="ProviderAssemblies"/>
/// — letting a new provider through without a dialect would re-open the per-provider quoting
/// drift the audit called out.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class SqlDialectGuardrailTests
{
    /// <summary>
    /// All shipped provider assemblies. Force-loaded via a type from each so the test runner
    /// (which does not necessarily preload every referenced assembly) sees them.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, System.Reflection.Assembly Assembly)> ProviderAssemblies = new[]
    {
        ("Honua.Postgres", typeof(Honua.Postgres.Queries.Filters.PostgresSqlDialect).Assembly),
        ("Honua.MySql", typeof(Honua.MySql.Queries.Filters.MySqlSqlDialect).Assembly),
        ("Honua.SqlServer", typeof(Honua.SqlServer.Queries.Filters.SqlServerSqlDialect).Assembly),
        ("Honua.DuckDB", typeof(Honua.DuckDB.Queries.Filters.DuckDbSqlDialect).Assembly),
        ("Honua.Oracle", typeof(Honua.Oracle.Queries.Filters.OracleSqlDialect).Assembly),
    };

    [ArchitectureTest]
    public void EachProviderAssembly_ShouldDeclareExactlyOneSqlDialectImplementation()
    {
        foreach (var (name, assembly) in ProviderAssemblies)
        {
            var dialects = assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Where(t => typeof(ISqlDialect).IsAssignableFrom(t))
                .ToList();

            dialects.Should().HaveCount(
                1,
                "provider assembly '{0}' must export exactly one ISqlDialect implementation " +
                "(found: {1}). Duplicate dialects cause identifier-quoting drift; a missing " +
                "dialect re-opens the SQL-injection boundary audit C4 was closing.",
                name,
                string.Join(", ", dialects.Select(d => d.FullName)));
        }
    }

    [ArchitectureTest]
    public void EachProviderDialect_ShouldExposeStableNameAndParameterPrefix()
    {
        // The dialects are exposed through static Instance singletons by convention; activate
        // via the parameterless constructor as a fallback.
        var observedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (assemblyName, assembly) in ProviderAssemblies)
        {
            var dialectType = assembly.GetTypes()
                .Single(t => !t.IsAbstract && typeof(ISqlDialect).IsAssignableFrom(t));

            var instance = (ISqlDialect?)(dialectType.GetField("Instance")?.GetValue(null)
                ?? Activator.CreateInstance(dialectType, nonPublic: true));
            instance.Should().NotBeNull("provider '{0}' must expose an ISqlDialect instance", assemblyName);

            instance!.Name.Should().NotBeNullOrWhiteSpace("dialect in '{0}' must report a stable name", assemblyName);
            observedNames.Add(instance.Name).Should().BeTrue(
                "dialect names must be unique across providers (duplicate detected for '{0}' from '{1}')",
                instance.Name,
                assemblyName);

            instance.ParameterPrefix.Should().NotBeNullOrEmpty(
                "dialect '{0}' must declare a parameter prefix", instance.Name);
            instance.ParameterPrefix.Length.Should().Be(
                1, "dialect '{0}' parameter prefix must be a single character", instance.Name);
        }
    }

    [ArchitectureTest]
    public void SqlFilterTranslators_ShouldDescendFromTheSharedExpressionVisitorBase()
    {
        // Constrained to the providers that actually ship a translator today. SqlServer and
        // DuckDB do not have one yet; when they grow one, add them here.
        var translatorTypes = new[]
        {
            typeof(Honua.Postgres.Queries.Filters.PostgresSqlFilterTranslator),
            typeof(Honua.MySql.Queries.Filters.MySqlSqlFilterTranslator),
        };

        foreach (var type in translatorTypes)
        {
            typeof(SqlFilterExpressionVisitorBase).IsAssignableFrom(type).Should().BeTrue(
                "filter translator '{0}' must descend from SqlFilterExpressionVisitorBase so " +
                "the recursion / depth-cap / parameter accumulation logic stays centralised " +
                "(audit C4 — security-relevant cleanup, not just elegance).",
                type.FullName);

            typeof(ISqlFilterTranslator).IsAssignableFrom(type).Should().BeTrue(
                "filter translator '{0}' must implement ISqlFilterTranslator", type.FullName);
        }
    }
}
