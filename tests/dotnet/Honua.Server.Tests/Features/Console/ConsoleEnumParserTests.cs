// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Console;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Whitelist checks reject undefined numeric enum values that
/// <c>JsonStringEnumConverter</c> would otherwise admit during deserialization.
/// </summary>
public class ConsoleEnumParserTests
{
    [UnitTest]
    public void IsDefined_ConsoleContentItemType_AcceptsAllDeclaredMembers()
    {
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.Service));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.Layer));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.SavedMap));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.Dashboard));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.Report));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.GeneratedApp));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentItemType.OpenData));
    }

    [UnitTest]
    public void IsDefined_ConsoleContentItemType_RejectsOutOfRange()
    {
        Assert.False(ConsoleEnumParser.IsDefined((ConsoleContentItemType)999));
        Assert.False(ConsoleEnumParser.IsDefined((ConsoleContentItemType)(-1)));
    }

    [UnitTest]
    public void IsDefined_ConsoleVisibility_AcceptsAllDeclaredMembers()
    {
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleVisibility.Personal));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleVisibility.Team));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleVisibility.Organization));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleVisibility.Public));
    }

    [UnitTest]
    public void IsDefined_ConsoleVisibility_RejectsOutOfRange()
    {
        Assert.False(ConsoleEnumParser.IsDefined((ConsoleVisibility)42));
    }

    [UnitTest]
    public void IsDefined_ConsoleContentAction_AcceptsAllDeclaredMembers()
    {
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.View));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.Edit));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.Publish));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.Share));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.Embed));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.Operate));
        Assert.True(ConsoleEnumParser.IsDefined(ConsoleContentAction.Administer));
    }

    [UnitTest]
    public void IsDefined_ConsoleContentAction_RejectsOutOfRange()
    {
        Assert.False(ConsoleEnumParser.IsDefined((ConsoleContentAction)999));
    }

    [UnitTest]
    public void IsDefined_MetadataV2LifecycleStatus_AcceptsAllDeclaredMembers()
    {
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2LifecycleStatus.Draft));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2LifecycleStatus.Active));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2LifecycleStatus.Deprecated));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2LifecycleStatus.Retired));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2LifecycleStatus.Archived));
    }

    [UnitTest]
    public void IsDefined_MetadataV2LifecycleStatus_RejectsOutOfRange()
    {
        Assert.False(ConsoleEnumParser.IsDefined((MetadataV2LifecycleStatus)999));
    }

    [UnitTest]
    public void IsDefined_MetadataV2OperationalState_AcceptsAllDeclaredMembers()
    {
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2OperationalState.Unknown));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2OperationalState.Ready));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2OperationalState.Pending));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2OperationalState.Degraded));
        Assert.True(ConsoleEnumParser.IsDefined(MetadataV2OperationalState.Failed));
    }

    [UnitTest]
    public void IsDefined_MetadataV2OperationalState_RejectsOutOfRange()
    {
        Assert.False(ConsoleEnumParser.IsDefined((MetadataV2OperationalState)999));
    }
}
