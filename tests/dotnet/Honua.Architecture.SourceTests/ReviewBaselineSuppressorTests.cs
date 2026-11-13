// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Honua.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class ReviewBaselineSuppressorTests
{
    private const string Root = "/repo";
    private const string Source = "class C { }";

    [Theory]
    [InlineData("CA2000")]
    [InlineData("IDE0063")]
    [InlineData("RS0030")]
    [InlineData("S1244")]
    [InlineData("S2486")]
    [InlineData("S108")]
    [InlineData("CA1508")]
    [InlineData("CA1851")]
    public async Task RecordedFinding_IsSuppressedEvenWithWarningsAsErrors(string id)
    {
        var diagnostics = await AnalyzeAsync(new Scenario(id, Source, "src/C.cs") { Baseline = Baseline(id) });
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("", Source, "src/C.cs")]
    [InlineData("malformed\trow", Source, "src/C.cs")]
    [InlineData(null, Source, "src/C.cs")]
    [InlineData("recorded", "class Changed { }", "src/C.cs")]
    [InlineData("recorded", Source, "src/New.cs")]
    public async Task UnrecordedFinding_RemainsAnError(string? baseline, string source, string path)
    {
        var diagnostics = await AnalyzeAsync(new Scenario("CA2000", source, path)
        { Baseline = baseline == "recorded" ? Baseline("CA2000") : baseline });
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task MovedFinding_PreservesItsBaseline()
    {
        Assert.Empty(await AnalyzeAsync(new Scenario("CA2000", "\n\n    " + Source,
            "src/C.cs") { Baseline = Baseline("CA2000") }));
    }

    [Fact]
    public async Task LinkedSourcePath_UsesTheCanonicalRepositoryPath()
    {
        Assert.Empty(await AnalyzeAsync(new Scenario("RS0030", Source,
            "tests/../src/C.cs") { Baseline = Baseline("RS0030") }));
    }

    [Fact]
    public async Task MoreOccurrencesThanRecorded_FailTheBuild()
    {
        var diagnostics = await AnalyzeAsync(new Scenario("CA2000", Source,
            "src/C.cs") { Baseline = Baseline("CA2000"), Count = 2 });
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public async Task DuplicateBaselineRows_CannotIncreaseBudget()
    {
        var baseline = Baseline("CA2000");
        baseline += baseline.Replace("\t1\n", "\t2\n", StringComparison.Ordinal);
        var diagnostics = await AnalyzeAsync(new Scenario("CA2000", Source, "src/C.cs") { Baseline = baseline, Count = 2 });
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public async Task AnotherDiagnosticId_CannotUseAnExistingBaseline()
    {
        Assert.Single(await AnalyzeAsync(new Scenario("CA1851", Source, "src/C.cs") { Baseline = Baseline("CA2000") }));
    }

    [Fact]
    public async Task ReplacementFinding_InAnotherMethod_CannotInheritBudget()
    {
        const string source = "class C { void Fresh() {\nthrow null!;\n} }";
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("Existing finding\nC/Old()\nthrow null!;")));
        var baseline = "CA2000\tsrc/C.cs\t" + hash + "\t1\n";
        Assert.Single(await AnalyzeAsync(new Scenario("CA2000", source, "src/C.cs") { Baseline = baseline }));
    }

    [Theory]
    [InlineData("class C { void Old() {", "C/Old()")]
    [InlineData("class C { static C() {", "C/static C()")]
    [InlineData("class C { C() {", "C/C()")]
    [InlineData("class C { event System.Action Old { add {", "C/Old/add")]
    [InlineData("class C { event System.Action I.Old { add {", "C/I.Old/add")]
    public async Task ExistingFinding_InItsRecordedMember_RemainsSuppressed(string opening, string scope)
    {
        var source = opening + "\nthrow null!;\n";
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("Existing finding\n" + scope + "\nthrow null!;")));
        var baseline = "CA2000\tsrc/C.cs\t" + hash + "\t1\n";
        Assert.Empty(await AnalyzeAsync(new Scenario("CA2000", source, "src/C.cs") { Baseline = baseline }));
    }

    [Theory]
    [InlineData("class C { static C() {", "C/C()")]
    [InlineData("class C { C() {", "C/static C()")]
    [InlineData("class C { event System.Action Fresh { add {", "C/Old/add")]
    [InlineData("class C { event System.Action I.Old { add {", "C/Old/add")]
    [InlineData("class C { event System.Action J.Old { add {", "C/I.Old/add")]
    [InlineData("class C { void Old<T>() {", "C/Old()")]
    [InlineData("class C<T> { void Old() {", "C/Old()")]
    [InlineData("class C { void I.Old() {", "C/Old()")]
    [InlineData("class C { void J.Old() {", "C/I.Old()")]
    [InlineData("class C { int I.Old { get {", "C/Old/get")]
    public async Task ReplacementMember_CannotInheritAnotherSignature(string opening, string oldScope)
    {
        var source = opening + "\nthrow null!;\n";
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("Existing finding\n" + oldScope + "\nthrow null!;")));
        var baseline = "CA2000\tsrc/C.cs\t" + hash + "\t1\n";
        Assert.Single(await AnalyzeAsync(new Scenario("CA2000", source, "src/C.cs") { Baseline = baseline }));
    }

    private static string Baseline(string id)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Existing finding\nC\n" + Source)));
        return id + "\tsrc/C.cs\t" + hash + "\t1\n";
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Scenario scenario)
    {
        var tree = CSharpSyntaxTree.ParseText(scenario.Source, path: Root + "/" + scenario.Path);
        var compilation = CSharpCompilation.Create("BaselineTest", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error));
        ImmutableArray<AdditionalText> files = scenario.Baseline is null ? [] :
            [new BaselineFile(scenario.Baseline)];
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
            new FindingAnalyzer(scenario.Id, scenario.Count), new ReviewBaselineSuppressor());
        return await compilation.WithAnalyzers(analyzers, new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
    }

    private sealed record Scenario(string Id, string Source, string Path)
    {
        public string? Baseline { get; init; }
        public int Count { get; init; } = 1;
    }

    private sealed class BaselineFile(string content) : AdditionalText
    {
        public override string Path => Root + "/scripts/ci/analyzer-baseline/AnalyzerBaseline.tsv";
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }

    // This in-memory test analyzer deliberately accepts each supported rule ID at runtime.
#pragma warning disable RS1017, RS1036, RS1041
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class FindingAnalyzer(string id, int count) : DiagnosticAnalyzer
    {
        private readonly DiagnosticDescriptor _rule = new(id, "Existing finding", "Existing finding",
            "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxTreeAction(treeContext =>
            {
                var root = treeContext.Tree.GetRoot(treeContext.CancellationToken);
                var marker = treeContext.Tree.GetText(treeContext.CancellationToken).ToString().IndexOf("throw", StringComparison.Ordinal);
                var start = marker >= 0 ? marker : root.DescendantTokens().First().SpanStart;
                for (var index = 0; index < count; index++)
                {
                    treeContext.ReportDiagnostic(Diagnostic.Create(_rule,
                        Location.Create(treeContext.Tree, new TextSpan(start + index, 1))));
                }
            });
        }
    }
#pragma warning restore RS1017, RS1036, RS1041
}
