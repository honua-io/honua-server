// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Holds the AOT contract for <c>GET /monitoring/health/comprehensive</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Honua.Server.csproj</c> sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so the
/// shipped server — every AOT image and every Lambda package — has no reflection fallback. The
/// endpoint's own write already goes through <see cref="MetricsJsonContext"/>, but the per-entry
/// <c>Data</c> projection built its values with the parameterless
/// <c>JsonSerializer.SerializeToElement(value)</c> overload, which resolves metadata through
/// <c>JsonSerializerOptions.Default</c>. That threw <see cref="InvalidOperationException"/> inside
/// the projection lambda — before the source-generated write could run — so the endpoint returned
/// 500 on every deployed build whenever any health check attached data, which is the normal case.
/// </para>
/// <para>
/// This test project runs on the xUnit host, where reflection serialization IS enabled, so an
/// integration test against the endpoint cannot see the defect: the reflection fallback simply
/// succeeds. The guard therefore has two halves — a direct assertion that the projection produces
/// the right JSON, and a call-site scan asserting no serializer call in the endpoint file omits its
/// source-generated type info. The scan is the half that actually catches a regression.
/// </para>
/// </remarks>
[Protocol(TestProtocols.Admin)]
public sealed class ComprehensiveHealthJsonReflectionSafetyTests
{
    private const string EndpointsSourcePath =
        "src/Honua.Server/Features/Infrastructure/Monitoring/ProductionMonitoringEndpoints.cs";

    // Mirrors the published image: source-generated metadata only, no reflection fallback.
    private static readonly JsonSerializerOptions AotOnlyOptions = new()
    {
        TypeInfoResolver = MetricsJsonContext.Default,
    };

    [UnitTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public void SanitizeHealthCheckData_ProjectsEverySupportedPrimitive()
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["text"] = "postgres",
            ["flag"] = true,
            ["byte"] = (byte)7,
            ["sbyte"] = (sbyte)-7,
            ["short"] = (short)-1234,
            ["ushort"] = (ushort)1234,
            ["int"] = -42,
            ["uint"] = 42u,
            ["long"] = -9_000_000_000L,
            ["ulong"] = ulong.MaxValue,
            ["float"] = 1.5f,
            ["double"] = 2.25d,
            ["decimal"] = 3.5m,
            ["unmapped"] = TimeSpan.FromSeconds(90),
        };

        var sanitized = ProductionMonitoringEndpoints.SanitizeHealthCheckData(data);

        sanitized.Should().NotBeNull();
        sanitized!["text"].GetString().Should().Be("postgres");
        sanitized["flag"].GetBoolean().Should().BeTrue();
        sanitized["byte"].GetByte().Should().Be(7);
        sanitized["sbyte"].GetSByte().Should().Be(-7);
        sanitized["short"].GetInt16().Should().Be(-1234);
        sanitized["ushort"].GetUInt16().Should().Be(1234);
        sanitized["int"].GetInt32().Should().Be(-42);
        sanitized["uint"].GetUInt32().Should().Be(42u);
        sanitized["long"].GetInt64().Should().Be(-9_000_000_000L);

        // ulong.MaxValue is why the integral arms cannot simply be widened to long.
        sanitized["ulong"].GetUInt64().Should().Be(ulong.MaxValue);

        // float keeps its own arm so 1.5f does not acquire double's extra digits.
        sanitized["float"].GetSingle().Should().Be(1.5f);
        sanitized["double"].GetDouble().Should().Be(2.25d);
        sanitized["decimal"].GetDecimal().Should().Be(3.5m);

        // Anything outside the mapped set degrades to its string form rather than throwing.
        sanitized["unmapped"].ValueKind.Should().Be(JsonValueKind.String);
    }

    [UnitTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public void SanitizeHealthCheckData_RedactsSensitiveKeys_AndReturnsNullForAnEmptyBag()
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ConnectionString"] = "Host=postgres;Password=hunter2",
            ["apiToken"] = "secret-value",
            ["database"] = "honua_dev",
        };

        var sanitized = ProductionMonitoringEndpoints.SanitizeHealthCheckData(data);

        sanitized.Should().NotBeNull();
        sanitized!["ConnectionString"].GetString().Should().Be("[redacted]");
        sanitized["apiToken"].GetString().Should().Be("[redacted]");
        sanitized["database"].GetString().Should().Be("honua_dev");

        ProductionMonitoringEndpoints.SanitizeHealthCheckData(new Dictionary<string, object>())
            .Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public void SanitizedData_SerializesThroughSourceGeneratedMetadataOnly()
    {
        var sanitized = ProductionMonitoringEndpoints.SanitizeHealthCheckData(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["database"] = "honua_dev",
            ["latencyMs"] = 12,
        });

        // GetTypeInfo throws when the resolver has no metadata for the type, so this fails rather
        // than silently falling back the way the reflection-enabled default would.
        var json = JsonSerializer.Serialize(
            sanitized,
            AotOnlyOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>)));

        json.Should().Contain("\"database\":\"honua_dev\"").And.Contain("\"latencyMs\":12");
    }

    [UnitTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public void MonitoringEndpoints_NeverCallTheReflectionBasedSerializerOverloads()
    {
        var source = File.ReadAllText(Path.Join(FindRepositoryRoot(), EndpointsSourcePath));

        var offenders = new List<string>();
        foreach (var member in new[] { "JsonSerializer.SerializeToElement(", "JsonSerializer.Serialize(" })
        {
            var index = source.IndexOf(member, StringComparison.Ordinal);
            while (index >= 0)
            {
                var call = ReadBalancedCall(source, index + member.Length - 1);
                if (!call.Contains("JsonContext", StringComparison.Ordinal)
                    && !call.Contains("JsonTypeInfo", StringComparison.Ordinal))
                {
                    offenders.Add(Collapse(call));
                }

                index = source.IndexOf(member, index + member.Length, StringComparison.Ordinal);
            }
        }

        offenders.Should().BeEmpty(
            "every serializer call in {0} must pass its source-generated JsonTypeInfo — the "
            + "parameterless overloads resolve through JsonSerializerOptions.Default and throw once "
            + "JsonSerializerIsReflectionEnabledByDefault=false, which is how the shipped server runs",
            EndpointsSourcePath);
    }

    /// <summary>Returns the argument list of a call, starting at its opening parenthesis.</summary>
    private static string ReadBalancedCall(string source, int openParenIndex)
    {
        var depth = 0;
        for (var cursor = openParenIndex; cursor < source.Length; cursor++)
        {
            if (source[cursor] == '(')
            {
                depth++;
            }
            else if (source[cursor] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openParenIndex..(cursor + 1)];
                }
            }
        }

        return source[openParenIndex..];
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace && lastWasSpace)
            {
                continue;
            }

            builder.Append(isSpace ? ' ' : character);
            lastWasSpace = isSpace;
        }

        return builder.ToString();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            // "Honua.sln" is a fixed relative literal, never absolute.
            if (File.Exists(Path.Join(directory.FullName, "Honua.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
