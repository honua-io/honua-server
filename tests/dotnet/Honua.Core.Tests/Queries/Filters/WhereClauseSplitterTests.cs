// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public class WhereClauseSplitterTests
{
    [UnitTest]
    public void SplitOnAnd_SplitsLogicalAndOutsideQuotes()
    {
        var parts = WhereClauseSplitter.SplitOnAnd("a = 1 AND b = 2");

        Assert.Equal(["a = 1 ", " b = 2"], parts);
    }

    [UnitTest]
    public void SplitOnAnd_KeepsAndInsideQuotedLiteral()
    {
        var parts = WhereClauseSplitter.SplitOnAnd("name = 'foo AND bar' AND b = 2");

        Assert.Equal(["name = 'foo AND bar' ", " b = 2"], parts);
    }

    [UnitTest]
    public void SplitOnAnd_KeepsEscapedQuoteInsideLiteral()
    {
        var parts = WhereClauseSplitter.SplitOnAnd("name = 'it''s AND fine' AND b = 2");

        Assert.Equal(["name = 'it''s AND fine' ", " b = 2"], parts);
    }

    [UnitTest]
    public void SplitOnAnd_DoesNotSplitIdentifierContainingAnd()
    {
        var parts = WhereClauseSplitter.SplitOnAnd("start_and_end = 1 AND and_flag = 2");

        Assert.Equal(["start_and_end = 1 ", " and_flag = 2"], parts);
    }
}
