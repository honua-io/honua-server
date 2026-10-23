// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private void AppendPagedFeatureSource(SqlBuilder sql, FeatureQuery query, bool probeLimit)
    {
        // Encode geometry and construct attribute JSON only for the page, not for
        // the potentially many rows consumed by OFFSET or a pre-pagination sort.
        // Project explicit raw columns: SELECT * would require privileges on
        // unrelated source columns and could pull large, unrequested values.
        var columns = new List<string> { _primaryKeyColumn };
        var names = new HashSet<string>(columns, StringComparer.Ordinal);
        void AddColumn(string column)
        {
            if (names.Add(column))
            {
                columns.Add(column);
            }
        }

        if (_geometryColumn is not null)
        {
            AddColumn(_geometryColumn);
        }

        if (!query.ExcludeAttributes)
        {
            var fields = ResolveAttributeFields(query);
            if (string.IsNullOrWhiteSpace(_mapping.AttributesColumn))
            {
                foreach (var field in fields)
                {
                    AddColumn(ValidateAndQuoteIdentifier(field.Name));
                }
            }
            else if (fields.Any(field => !IsObjectIdField(field.Name) && !field.SemanticRoles.Contains("id.primary")))
            {
                AddColumn(ValidateAndQuoteIdentifier(_mapping.AttributesColumn));
            }
        }

        // Evaluate each typed sort expression once, then refer to its value in
        // both orders. Qualified outer aliases prevent response column names
        // (notably "attributes") from shadowing physical source fields. No SQL
        // parsing is needed, including for KNN and datum transformation literals.
        var innerOrder = new List<string>();
        var outerOrder = new List<string>();
        var aliasIndex = 0;
        foreach (var (expression, suffix) in BuildOrderExpressions(sql, query))
        {
            string alias;
            do
            {
                alias = ValidateAndQuoteIdentifier("__honua_page_order_" + (aliasIndex++).ToString(CultureInfo.InvariantCulture));
            } while (!names.Add(alias));

            columns.Add($"{expression} AS {alias}");
            innerOrder.Add(alias + suffix);
            outerOrder.Add("page_source." + alias + suffix);
        }

        sql.Append(CultureInfo.InvariantCulture,
            $" FROM (SELECT {string.Join(", ", columns)} FROM {BuildFeatureSource(query, sql)}");
        // All user, layer, branch and security filters must precede pagination.
        AppendFilter(sql, query);
        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {string.Join(", ", innerOrder)}");
        AppendPagination(sql, query, probeLimit);
        sql.Append(CultureInfo.InvariantCulture,
            $") AS page_source ORDER BY {string.Join(", ", outerOrder)}");
    }
}
