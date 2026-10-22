// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Workflow;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Workflow;

[Protocol(ProtocolNames.TestQuality)]
[Operation(Operations.Query)]
public sealed class WorkflowResolverTests
{
    private readonly WorkflowBundle _bundle = WorkflowBundle.LoadDefault();

    [UnitTest]
    public void Resolve_AmbiguousSource_RequiresExplicitTurnChoice()
    {
        var resolver = new WorkflowResolver(_bundle);
        var result = resolver.Resolve(new("use the source", ["dataset", "connection"]));
        Assert.Equal("clarification", result.Status);
        Assert.Equal("source-meaning", result.Clarification!.Id);
        Assert.Equal("candidate", resolver.Resolve(new("use the source", ["dataset", "connection"], ClarificationId: "source-meaning", Choice: "connection")).Status);
        Assert.Equal("clarification", resolver.Resolve(new("use the source", ["dataset"], ClarificationId: "other", Choice: "dataset")).Status);
    }

    [UnitTest]
    public void Resolve_LongestPhrase_PrecedesGrantFiltering()
    {
        var resolver = new WorkflowResolver(_bundle);
        Assert.Equal("no-match", resolver.Resolve(new("database connection", ["dataset"])).Status);
        var result = resolver.Resolve(new("please use the database connection", ["connection"]));
        Assert.Equal("connection", Assert.Single(result.Candidates).TermId);
        Assert.Equal("no-match", resolver.Resolve(new("disconnection", ["connection"])).Status);
    }

    [UnitTheory]
    [InlineData("publish the layer")]
    [InlineData("publish the data")]
    [InlineData("publish")]
    public void Resolve_PublicationPhrase_RequiresClarification(string text)
    {
        var result = new WorkflowResolver(_bundle).Resolve(new(text, ["service", "publication"]));
        Assert.Equal("clarification", result.Status);
        Assert.Contains("service", result.Clarification!.Choices);
        Assert.Contains("publication", result.Clarification.Choices);
    }

    [UnitTest]
    public void Resolve_SchemaField_DoesNotRewriteSource()
    {
        Assert.Equal("no-match", new WorkflowResolver(_bundle).Resolve(new("source.kind", ["dataset", "connection"])).Status);
    }

    [UnitTest]
    public void Resolve_UnauthorizedChoice_PreservesClarification()
    {
        var result = new WorkflowResolver(_bundle).Resolve(new("source", ["dataset"], ClarificationId: "source-meaning", Choice: "connection"));
        Assert.Equal("clarification", result.Status);
        Assert.Equal(["dataset"], result.Clarification!.Choices);
    }

    [UnitTest]
    public void SuggestPathways_MissingPrerequisite_ExposesExecutorBlocker()
    {
        var result = _bundle.SuggestPathways([], ["service.publish"], ["serve.geoservices-featureserver"]);
        var pathway = Assert.Single(result);
        Assert.Equal(["dataset"], pathway.MissingPrerequisites);
        Assert.Equal("executor-unavailable", pathway.Blocker);
        Assert.Empty(_bundle.SuggestPathways([], ["layer.publish"], ["admin.control-plane"]));
        Assert.Empty(_bundle.SuggestPathways(["dataset"], [], ["serve.geoservices-featureserver"]));
    }

    [UnitTest]
    public void GetEffectTier_StyleTarget_ClassifiesActualEffect()
    {
        Assert.Equal("working-draft", _bundle.GetEffectTier("style.apply", "working-draft"));
        Assert.Equal("server-state", _bundle.GetEffectTier("style.apply", "shared-catalog"));
        Assert.Throws<ArgumentException>(() => _bundle.GetEffectTier("style.apply", "unknown"));
    }
}
