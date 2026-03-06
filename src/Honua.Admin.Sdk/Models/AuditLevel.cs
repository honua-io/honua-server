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

namespace Honua.Admin.Sdk.Models;

/// <summary>
/// Audit logging levels for administrative operations.
/// </summary>
public enum AuditLevel
{
    /// <summary>
    /// No audit logging.
    /// </summary>
    None = 0,

    /// <summary>
    /// Basic audit logging for major operations.
    /// </summary>
    Basic = 1,

    /// <summary>
    /// Standard audit logging for most operations.
    /// </summary>
    Standard = 2,

    /// <summary>
    /// Detailed audit logging including all field changes.
    /// </summary>
    Detailed = 3,

    /// <summary>
    /// Full audit logging including all operations and system events.
    /// </summary>
    Full = 4
}