// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Identity;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Identity;

/// <summary>
/// Docker-free coverage of the principal-lookup SQL. The ranking expression must
/// not sort NULL first, and the issuer-less predicate must be parenthesized.
/// </summary>
public sealed class PostgresUserStorePrincipalLookupSqlTests
{
    [UnitTest]
    public void SRV_DB_PrincipalLookup_WhereIsParenthesizedAndRankIsNullSafe()
    {
        PostgresUserStore.IssuerlessPrincipalWhere.Should().Be(
            "WHERE (u.external_id = @id AND u.external_issuer IS NULL) OR LOWER(u.user_id) = LOWER(@id)");
        PostgresUserStore.IssuerScopedPrincipalWhere.Should().Contain("(u.external_id = @id AND u.external_issuer = @issuer)");
        PostgresUserStore.PrincipalExternalIdRank.Should().Be(
            "ORDER BY COALESCE(u.external_id = @id, false) DESC");
        PostgresUserStore.PrincipalExternalIdRank.Should().NotContain("NULLS");
    }
}
