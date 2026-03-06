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
/// Spatial bounding box/envelope.
/// </summary>
public class Envelope
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    public double XMin { get; set; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    public double YMin { get; set; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    public double XMax { get; set; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    public double YMax { get; set; }

    /// <summary>
    /// Minimum Z coordinate.
    /// </summary>
    public double? ZMin { get; set; }

    /// <summary>
    /// Maximum Z coordinate.
    /// </summary>
    public double? ZMax { get; set; }

    /// <summary>
    /// Spatial reference system.
    /// </summary>
    public SpatialReference? SpatialReference { get; set; }
}