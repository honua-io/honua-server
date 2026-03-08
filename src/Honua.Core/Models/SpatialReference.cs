// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Honua.Core.Models;

/// <summary>
/// Spatial reference system information.
/// </summary>
public class SpatialReference
{
    /// <summary>
    /// Well-known ID (WKID) of the spatial reference system.
    /// </summary>
    public int WKID { get; set; }

    /// <summary>
    /// Latest well-known ID if different from WKID.
    /// </summary>
    public int? LatestWKID { get; set; }

    /// <summary>
    /// Well-known text representation.
    /// </summary>
    public string? WKT { get; set; }

    /// <summary>
    /// Vertical coordinate system ID.
    /// </summary>
    public int? VCSID { get; set; }

    /// <summary>
    /// Latest vertical coordinate system ID.
    /// </summary>
    public int? LatestVCSID { get; set; }
}