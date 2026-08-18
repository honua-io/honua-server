// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.ServiceDefaults;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Repo-wide sibling of <see cref="SloMetricContractTests"/> (honua-server#3323).
/// <para>
/// #3322 fixed the three SLO instruments. This guard covers EVERY instrument the repository
/// declares, because the mechanism that broke those three is not specific to them: the OTel
/// Prometheus exporter derives the exported series name from the instrument name AND its
/// <c>unit</c>, mapping the unit through the UCUM table and appending it when the name does not
/// already end in the mapped form. A declared unit therefore renames the series out from under
/// every dashboard, alert rule and runbook <b>without breaking a single build</b>. In PromQL an
/// absent series is not an error: <c>rate()</c> over it is an empty vector, a ratio against it is
/// empty, the panel renders blank and the alert never fires. The failure mode is SILENCE.
/// </para>
/// <para>
/// Three assertions close the loop, and all three have to pass for the loop to be closed:
/// <list type="number">
/// <item><description>the checked-in inventory <c>observability/metric-name-contract.json</c>
/// still describes the instruments the source tree actually declares (a new instrument, or a
/// changed unit, reddens here);</description></item>
/// <item><description>every series name in that inventory is the name a reader would derive from
/// the instrument name, i.e. no entry launders a mangled name into the contract;</description></item>
/// <item><description>the REAL Prometheus exposition of a REAL running server agrees, for every
/// instrument, that the declared unit produces that series name — reasoning about the UCUM table
/// is exactly the step that failed before.</description></item>
/// </list>
/// </para>
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection("Performance")]
public sealed class MetricNameContractTests : IClassFixture<TestWebApplicationFactory>
{
    private const string AdminPassword = "metric-name-contract-admin-key";

    /// <summary>
    /// Prefix applied to the probe instruments the exposition test registers. Probing under a
    /// distinct name keeps the guard from colliding with the identically named production
    /// instrument (a duplicate registration with a different type is dropped by OpenTelemetry with
    /// only a log line, which would make this test silently vacuous — the very failure mode it
    /// exists to catch). The prefix is a plain name prefix, so it cannot interact with the
    /// unit-suffix or <c>_total</c>-suffix logic under test.
    /// </summary>
    private const string ProbePrefix = "honua_metric_name_probe_";

    private static readonly Regex CreateInstrumentCall = new(
        @"Create(?<kind>Counter|Histogram|UpDownCounter|ObservableCounter|ObservableGauge|ObservableUpDownCounter|Gauge)\s*(<[^(]*>)?\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConstStringDeclaration = new(
        @"(?:public|internal|private|protected)\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]*)""\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(
        @"^""(?<value>(?:[^""\\]|\\.)*)""$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HonuaSeriesReference = new(
        @"honua_[a-z0-9_]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex YamlExpression = new(
        @"^\s*expr:\s*(?<expr>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly WebApplicationFactory<Program> _factory;

    public MetricNameContractTests(TestWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void SourceTree_InstrumentDeclarations_MatchTheContract()
    {
        var declared = ScanSourceTree();
        var contract = LoadContract();

        var declaredKeys = declared.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var contractKeys = contract.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        contractKeys.Except(declaredKeys).Should().BeEmpty(
            "observability/metric-name-contract.json lists instruments the source tree no longer "
            + "declares. A removed instrument means every consumer of its series is now querying an "
            + "empty vector; delete the entry only after the consumers are gone.");

        declaredKeys.Except(contractKeys).Should().BeEmpty(
            "every instrument this repository declares must be recorded in "
            + "observability/metric-name-contract.json with the Prometheus series it exports. An "
            + "unrecorded instrument is exactly how a unit-mangled name reaches production unnoticed: "
            + "nothing fails, the series is simply named something no dashboard asks for. Add an entry "
            + "(instrument, kind, unit, series, source).");

        foreach (var instrument in declared)
        {
            var entry = contract.SingleOrDefault(c => c.Key == instrument.Key);
            entry.Should().NotBeNull();
            entry!.Unit.Should().Be(
                instrument.Unit,
                "the declared OpenTelemetry unit of '{0}' changed, and the unit is part of the "
                + "EXPORTED SERIES NAME, not just documentation. Re-verify the exported name against a "
                + "real /metrics scrape and update every dashboard, alert rule and runbook that names "
                + "it before updating this contract.",
                instrument.Name);
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Contract_SeriesNames_AreTheNamesAReaderWouldDeriveFromTheInstrument()
    {
        var contract = LoadContract();

        foreach (var entry in contract)
        {
            entry.Series.Should().BeEquivalentTo(
                ExpectedSeries(entry.Kind, entry.Name),
                "'{0}' must export under the name a reader of the source would expect. A contract "
                + "entry that records a mangled name (…_errors_total, …_milliseconds_count, …_MiB) "
                + "would legalise the drift instead of catching it: fix the instrument — usually by "
                + "dropping a redundant `unit` — rather than the contract.",
                entry.Name);
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /metrics")]
    public async Task PrometheusExposition_ExportsEveryContractInstrumentUnderItsContractSeriesName()
    {
        var contract = LoadContract();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        // Force the host — and with it the MeterProvider that subscribes to the "Honua" meter —
        // up before the probe instruments are created, so their measurements are aggregated.
        (await client.GetAsync("/healthz/live")).EnsureSuccessStatusCode();

        // The probes are created on the SAME meter name the exporter is registered against, so
        // they travel the real exporter, not a re-implementation of its UCUM rules.
        using var meter = new Meter(HonuaTelemetry.ServiceName, HonuaTelemetry.ServiceVersion);
        // LIFETIME ANCHOR, not a result set. Observable instruments are only scraped while a
        // strong reference to them survives; without this list the GC is free to collect them
        // between creation and the /metrics scrape below, and every observable-kind entry would
        // silently drop out of the exposition — turning this guard green over metrics it never
        // actually checked. Its contents are deliberately never read; do not "clean it up".
        var observableProbes = new List<object>();

        foreach (var entry in contract)
        {
            var probeName = ProbePrefix + entry.Name;
            switch (entry.Kind)
            {
                case "Counter":
                    meter.CreateCounter<long>(probeName, entry.Unit, "metric-name contract probe").Add(1);
                    break;
                case "UpDownCounter":
                    meter.CreateUpDownCounter<long>(probeName, entry.Unit, "metric-name contract probe").Add(1);
                    break;
                case "Histogram":
                    meter.CreateHistogram<double>(probeName, entry.Unit, "metric-name contract probe").Record(1);
                    break;
                case "Gauge":
                    meter.CreateGauge<double>(probeName, entry.Unit, "metric-name contract probe").Record(1);
                    break;
                case "ObservableGauge":
                    observableProbes.Add(meter.CreateObservableGauge(
                        probeName, () => 1L, entry.Unit, "metric-name contract probe"));
                    break;
                case "ObservableCounter":
                    observableProbes.Add(meter.CreateObservableCounter(
                        probeName, () => 1L, entry.Unit, "metric-name contract probe"));
                    break;
                case "ObservableUpDownCounter":
                    observableProbes.Add(meter.CreateObservableUpDownCounter(
                        probeName, () => 1L, entry.Unit, "metric-name contract probe"));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"observability/metric-name-contract.json declares instrument kind "
                        + $"'{entry.Kind}' for '{entry.Name}', which this guard cannot probe. Teach "
                        + "the guard the new kind rather than dropping the entry.");
            }
        }

        var response = await client.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exported = ParseSeriesNames(await response.Content.ReadAsStringAsync());

        var drifted = new List<string>();
        foreach (var entry in contract)
        {
            foreach (var series in entry.Series)
            {
                var probeSeries = ProbePrefix + series;
                if (!exported.Contains(probeSeries))
                {
                    var actual = exported
                        .Where(name => name.StartsWith(ProbePrefix + Sanitize(entry.Name), StringComparison.Ordinal))
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();

                    drifted.Add(
                        $"{entry.Name} (kind {entry.Kind}, unit {entry.Unit ?? "null"}): contract says "
                        + $"'{series}', exporter produced [{string.Join(", ", actual.Select(a => a[ProbePrefix.Length..]))}]");
                }
            }
        }

        drifted.Should().BeEmpty(
            "the running server's real /metrics exposition disagrees with "
            + "observability/metric-name-contract.json. This is the assertion that a declared "
            + "OpenTelemetry `unit` cannot slip past: the exporter appends the UCUM-mapped unit to "
            + "the series name, so the metric consumers query stops existing and every PromQL "
            + "expression over it silently returns an empty vector.{0}Drift:{0}  {1}",
            Environment.NewLine,
            string.Join(Environment.NewLine + "  ", drifted));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void CommittedPromqlExpressions_OnlyReferenceSeriesTheServerExports()
    {
        // The consumer half of the guard. Renaming a series is only half a break; the other half is
        // a dashboard or alert rule left pointing at the old name, which Prometheus answers with an
        // empty vector and no error. Every `honua_`-prefixed identifier used inside a committed
        // PromQL expression must therefore resolve to a series this repository exports, to a label
        // name, or to an explicitly recorded known gap. Only expressions are scanned: prose in a
        // panel description may name a metric freely.
        var contract = LoadContract();
        var known = contract.SelectMany(entry => entry.Series).ToHashSet(StringComparer.Ordinal);
        known.UnionWith(ContractStringArray("label_names", "names"));
        known.UnionWith(ContractNotEmittedSeries());

        var unresolved = new List<string>();
        var scanned = 0;

        foreach (var (path, expression) in CommittedPromqlExpressions())
        {
            scanned++;
            foreach (Match reference in HonuaSeriesReference.Matches(expression))
            {
                if (!known.Contains(reference.Value))
                {
                    unresolved.Add($"{path}: '{reference.Value}' in `{expression.Trim()}`");
                }
            }
        }

        scanned.Should().BeGreaterThan(
            0, "finding no expressions to check would make this guard vacuously green");

        unresolved.Should().BeEmpty(
            "a committed dashboard panel or alert rule references a series name the server does not "
            + "export. Prometheus returns an empty vector for it: the panel renders blank, the alert "
            + "never fires, and nothing anywhere reports the problem. Point the expression at the "
            + "exported name in observability/metric-name-contract.json, or record the gap under "
            + "`not_emitted` if the instrument genuinely does not exist yet.{0}Unresolved:{0}  {1}",
            Environment.NewLine,
            string.Join(Environment.NewLine + "  ", unresolved.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The Prometheus series a given instrument produces, per the exporter's naming rules
    /// (documented in the <c>_rules</c> field of the contract and verified end to end by
    /// <see cref="PrometheusExposition_ExportsEveryContractInstrumentUnderItsContractSeriesName"/>).
    /// </summary>
    private static string[] ExpectedSeries(string kind, string instrument)
    {
        var name = Sanitize(instrument);

        return kind switch
        {
            "Counter" or "ObservableCounter" =>
                [name.EndsWith("_total", StringComparison.Ordinal) ? name : name + "_total"],
            // A Prometheus histogram never emits a sample under its base name.
            "Histogram" => [name + "_bucket", name + "_count", name + "_sum"],
            _ => [name],
        };
    }

    /// <summary>Applies the exporter's metric-name sanitization (dots and dashes become underscores).</summary>
    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '_' or ':' ? character : '_');
        }

        return builder.ToString();
    }

    private static HashSet<string> ParseSeriesNames(string exposition)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in exposition.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var cut = line.IndexOfAny(['{', ' ']);
            if (cut > 0)
            {
                names.Add(line[..cut]);
            }
        }

        return names;
    }

    private static ContractEntry[] LoadContract()
    {
        var path = RepositoryPaths.Resolve("observability", "metric-name-contract.json");
        File.Exists(path).Should().BeTrue("the metric-name contract is the inventory of record at {0}", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.GetProperty("instruments").EnumerateArray()
            .Select(entry => new ContractEntry(
                entry.GetProperty("instrument").GetString() ?? string.Empty,
                entry.GetProperty("kind").GetString() ?? string.Empty,
                entry.GetProperty("unit").ValueKind == JsonValueKind.Null
                    ? null
                    : entry.GetProperty("unit").GetString(),
                [.. entry.GetProperty("series").EnumerateArray().Select(series => series.GetString() ?? string.Empty)]))
            .ToArray();

        entries.Should().NotBeEmpty("an empty contract would make every assertion here vacuously green");
        return entries;
    }

    /// <summary>
    /// Every <c>Create*</c> instrument declaration in <c>src/</c> with a literal (or
    /// <c>const string</c>-resolvable) name, paired with the unit it declares.
    /// </summary>
    private static ContractEntry[] ScanSourceTree()
    {
        var sourceRoot = RepositoryPaths.Resolve("src");
        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var sources = files.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        // Instrument names are frequently held in `const string` fields so tests can subscribe to
        // the same identifier the instrument registers under; resolve them by simple name.
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in sources.Values)
        {
            foreach (Match match in ConstStringDeclaration.Matches(text))
            {
                constants[match.Groups["name"].Value] = match.Groups["value"].Value;
            }
        }

        var found = new Dictionary<string, ContractEntry>(StringComparer.Ordinal);
        foreach (var (path, text) in sources)
        {
            foreach (Match match in CreateInstrumentCall.Matches(text))
            {
                var open = match.Index + match.Length - 1;
                var close = FindMatchingParenthesis(text, open);
                if (close < 0)
                {
                    continue;
                }

                var arguments = SplitArguments(text[(open + 1)..close]);
                if (arguments.Count == 0)
                {
                    continue;
                }

                var name = ResolveName(arguments[0], constants);
                if (name is null)
                {
                    // Generic helpers such as PerformanceMetrics.CreateCounter(string name, ...)
                    // take the name from a parameter; the concrete call sites are matched instead.
                    continue;
                }

                var kind = match.Groups["kind"].Value;
                var unit = ResolveUnit(kind, arguments);
                var entry = new ContractEntry(name, kind, unit, ExpectedSeries(kind, name));
                found.TryAdd(entry.Key, entry);
            }
        }

        return [.. found.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    private static string? ResolveName(string argument, Dictionary<string, string> constants)
    {
        var trimmed = argument.Trim();
        var literal = StringLiteral.Match(trimmed);
        if (literal.Success)
        {
            return literal.Groups["value"].Value;
        }

        var simpleName = trimmed[(trimmed.LastIndexOf('.') + 1)..].Trim();
        return constants.TryGetValue(simpleName, out var value) ? value : null;
    }

    private static string? ResolveUnit(string kind, List<string> arguments)
    {
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index].TrimStart();
            if (!argument.StartsWith("unit:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = argument["unit:".Length..].Trim();
            var named = StringLiteral.Match(value);
            return named.Success ? named.Groups["value"].Value : null;
        }

        // Positional: Create<X>(name, unit, description) — or (name, callback, unit, description)
        // for observable instruments. A trailing single argument is the description, not the unit.
        var unitIndex = kind.StartsWith("Observable", StringComparison.Ordinal) ? 2 : 1;
        if (arguments.Count <= unitIndex + 1)
        {
            return null;
        }

        var positional = StringLiteral.Match(arguments[unitIndex].Trim());
        return positional.Success ? positional.Groups["value"].Value : null;
    }

    private static int FindMatchingParenthesis(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
                default:
                    break;
            }
        }

        return -1;
    }

    private static List<string> SplitArguments(string arguments)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inString = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (inString)
            {
                current.Append(character);
                if (character == '\\' && index + 1 < arguments.Length)
                {
                    current.Append(arguments[++index]);
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    current.Append(character);
                    continue;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                default:
                    break;
            }

            current.Append(character);
        }

        if (current.ToString().Trim().Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>
    /// Every PromQL expression committed to this repository: Grafana panel/alert <c>expr</c>
    /// fields and Prometheus rule <c>expr:</c> entries.
    /// </summary>
    private static IEnumerable<(string Path, string Expression)> CommittedPromqlExpressions()
    {
        var dashboards = RepositoryPaths.Resolve("docker", "monitoring", "grafana", "dashboards");
        if (Directory.Exists(dashboards))
        {
            foreach (var path in Directory.EnumerateFiles(dashboards, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var expression in JsonExpressions(document.RootElement))
                {
                    yield return (Path.GetFileName(path), expression);
                }
            }
        }

        var ruleFiles = new List<string>();
        var prometheus = RepositoryPaths.Resolve("docker", "monitoring", "prometheus");
        if (Directory.Exists(prometheus))
        {
            ruleFiles.AddRange(Directory.EnumerateFiles(prometheus, "*.yml"));
        }

        ruleFiles.Add(RepositoryPaths.Resolve("docs", "guides", "deploy", "examples", "prometheus-alerts.yml"));

        foreach (var path in ruleFiles.Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal))
        {
            foreach (Match match in YamlExpression.Matches(File.ReadAllText(path)))
            {
                yield return (Path.GetFileName(path), match.Groups["expr"].Value);
            }
        }
    }

    private static IEnumerable<string> JsonExpressions(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("expr") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString() ?? string.Empty;
                        continue;
                    }

                    foreach (var nested in JsonExpressions(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in JsonExpressions(item))
                    {
                        yield return nested;
                    }
                }

                break;
            default:
                break;
        }
    }

    private static string[] ContractStringArray(params string[] propertyPath)
    {
        var path = RepositoryPaths.Resolve("observability", "metric-name-contract.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var element = document.RootElement;
        foreach (var segment in propertyPath)
        {
            element = element.GetProperty(segment);
        }

        return [.. element.EnumerateArray().Select(item => item.GetString() ?? string.Empty)];
    }

    private static string[] ContractNotEmittedSeries()
    {
        var path = RepositoryPaths.Resolve("observability", "metric-name-contract.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("not_emitted").EnumerateArray()
            .Select(entry => entry.GetProperty("series").GetString() ?? string.Empty)];
    }

    private sealed record ContractEntry(string Name, string Kind, string? Unit, IReadOnlyList<string> Series)
    {
        internal string Key => $"{Name}|{Kind}";
    }
}
