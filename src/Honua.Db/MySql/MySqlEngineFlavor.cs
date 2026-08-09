// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.MySql;

/// <summary>
/// Engine flavor for the MySQL/MariaDB read/query provider.
/// Controls SQL emitted where MySQL 8 and MariaDB 10.6 diverge, most importantly
/// the WKB axis-order option that MySQL accepts on <c>ST_AsWKB</c> /
/// <c>ST_GeomFromWKB</c> but MariaDB does not.
/// </summary>
public enum MySqlEngineFlavor
{
    /// <summary>
    /// Oracle MySQL 8.0.11+. The provider emits the <c>'axis-order=long-lat'</c>
    /// option on <c>ST_AsWKB</c> and <c>ST_GeomFromWKB</c> so geographic SRSes
    /// (e.g. EPSG:4326) produce and consume canonical X/Y WKB instead of the
    /// SRS-defined latitude-first order. The option is permitted-but-ignored on
    /// projected SRSes.
    /// </summary>
    Mysql = 0,

    /// <summary>
    /// MariaDB 10.6+. The provider uses the 2-argument
    /// <c>ST_GeomFromWKB(wkb, srid)</c> form (the third axis-order argument is
    /// MySQL-only). MariaDB does not enforce SRS-based axis order, so WKB-natural
    /// X/Y already matches the canonical Honua contract.
    /// </summary>
    MariaDb = 1
}

/// <summary>
/// Reference-type holder for <see cref="MySqlEngineFlavor"/> so the resolved flavor
/// can be registered as a singleton via <c>AddSingleton&lt;T&gt;(T)</c>.
/// </summary>
internal sealed class MySqlEngineFlavorHolder
{
    public MySqlEngineFlavorHolder(MySqlEngineFlavor flavor) => Flavor = flavor;

    public MySqlEngineFlavor Flavor { get; }
}
