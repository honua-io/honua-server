using Honua.Core.Features.Workflow;

namespace Honua.Core.Tests.Features.Workflow;

public sealed class WorkflowResolverTests
{
    private readonly WorkflowBundle _bundle = WorkflowBundle.LoadDefault();

    [Fact]
    public void SourceAlwaysRequiresExplicitTurnChoice()
    {
        var resolver = new WorkflowResolver(_bundle);
        var result = resolver.Resolve(new("use the source", ["dataset", "connection"]));
        Assert.Equal("clarification", result.Status);
        Assert.Equal("source-meaning", result.Clarification!.Id);
        Assert.Equal("candidate", resolver.Resolve(new("use the source", ["dataset", "connection"], ClarificationId: "source-meaning", Choice: "connection")).Status);
        Assert.Equal("clarification", resolver.Resolve(new("use the source", ["dataset"], ClarificationId: "other", Choice: "dataset")).Status);
    }

    [Fact]
    public void LongestPhrasePrecedesGrantFiltering()
    {
        var resolver = new WorkflowResolver(_bundle);
        Assert.Equal("no-match", resolver.Resolve(new("database connection", ["dataset"])).Status);
        var result = resolver.Resolve(new("please use the database connection", ["connection"]));
        Assert.Equal("connection", Assert.Single(result.Candidates).TermId);
        Assert.Equal("no-match", resolver.Resolve(new("disconnection", ["connection"])).Status);
    }

    [Theory]
    [InlineData("publish the layer")]
    [InlineData("publish the data")]
    [InlineData("publish")]
    public void PublicationNeverSilentlySelectsCatalogMutation(string text)
    {
        var result = new WorkflowResolver(_bundle).Resolve(new(text, ["service", "publication"]));
        Assert.Equal("clarification", result.Status);
        Assert.Contains("service", result.Clarification!.Choices);
        Assert.Contains("publication", result.Clarification.Choices);
    }

    [Fact]
    public void SchemaFieldIsNotRewrittenAsConversationalSource()
    {
        Assert.Equal("no-match", new WorkflowResolver(_bundle).Resolve(new("source.kind", ["dataset", "connection"])).Status);
    }

    [Fact]
    public void UnauthorizedChoiceCannotEscapeClarification()
    {
        var result = new WorkflowResolver(_bundle).Resolve(new("source", ["dataset"], ClarificationId: "source-meaning", Choice: "connection"));
        Assert.Equal("clarification", result.Status);
        Assert.Equal(["dataset"], result.Clarification!.Choices);
    }

    [Fact]
    public void PathwaysExposeOneMissingPrerequisiteAndUnavailableExecutor()
    {
        var result = _bundle.SuggestPathways([], ["service.publish"], ["serve.geoservices-featureserver"]);
        var pathway = Assert.Single(result);
        Assert.Equal(["dataset"], pathway.MissingPrerequisites);
        Assert.Equal("executor-unavailable", pathway.Blocker);
        Assert.Empty(_bundle.SuggestPathways([], ["layer.publish"], ["admin.control-plane"]));
        Assert.Empty(_bundle.SuggestPathways(["dataset"], [], ["serve.geoservices-featureserver"]));
    }

    [Fact]
    public void EffectsDependOnActualTarget()
    {
        Assert.Equal("working-draft", _bundle.GetEffectTier("style.apply", "working-draft"));
        Assert.Equal("server-state", _bundle.GetEffectTier("style.apply", "shared-catalog"));
        Assert.Throws<ArgumentException>(() => _bundle.GetEffectTier("style.apply", "unknown"));
    }
}
