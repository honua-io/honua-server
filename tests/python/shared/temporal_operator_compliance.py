# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Comprehensive temporal operator compliance test cases for enhanced OGC standards support.
"""

from __future__ import annotations


# All temporal operators per OGC Filter Encoding 2.0 and Allen's interval algebra
ENHANCED_TEMPORAL_OPERATORS: list[tuple[str, str, str]] = [
    # Basic temporal operators
    ("after_instant", "T_AFTER(created_at, TIMESTAMP('2024-01-01T00:00:00Z'))", "Basic temporal after"),
    ("before_instant", "T_BEFORE(updated_at, TIMESTAMP('2024-12-31T23:59:59Z'))", "Basic temporal before"),
    ("during_period", "T_DURING(event_time, INTERVAL('2024-01-01T00:00:00Z', '2024-12-31T23:59:59Z'))", "Basic temporal during"),
    ("equals_instant", "T_EQUALS(timestamp_field, TIMESTAMP('2024-06-15T12:00:00Z'))", "Temporal equality"),

    # Allen's interval relations - comprehensive set
    ("contains_interval", "T_CONTAINS(project_duration, INTERVAL('2024-02-01T00:00:00Z', '2024-03-01T00:00:00Z'))", "Temporal contains relation"),
    ("overlaps_interval", "T_OVERLAPS(event_period, INTERVAL('2024-01-15T00:00:00Z', '2024-02-15T00:00:00Z'))", "Temporal overlaps relation"),
    ("meets_instant", "T_MEETS(first_period, second_period)", "Temporal meets relation"),
    ("overlapped_by", "T_OVERLAPPEDBY(meeting_time, work_hours)", "Temporal overlapped by relation"),
    ("met_by", "T_METBY(event_end, next_event_start)", "Temporal met by relation"),
    ("starts_period", "T_STARTS(sub_event, main_event)", "Temporal starts relation"),
    ("started_by", "T_STARTEDBY(main_event, sub_event)", "Temporal started by relation"),
    ("finishes_period", "T_FINISHES(cleanup_phase, project)", "Temporal finishes relation"),
    ("finished_by", "T_FINISHEDBY(project, cleanup_phase)", "Temporal finished by relation"),

    # Additional temporal predicates
    ("intersects_period", "T_INTERSECTS(availability, booking_period)", "Temporal intersects relation"),
    ("disjoint_periods", "T_DISJOINT(maintenance_window, operation_hours)", "Temporal disjoint relation"),
]

# Complex temporal expressions combining multiple operators
COMPLEX_TEMPORAL_EXPRESSIONS: list[tuple[str, str, str]] = [
    (
        "compound_temporal_and",
        "T_AFTER(start_date, TIMESTAMP('2024-01-01T00:00:00Z')) AND T_BEFORE(end_date, TIMESTAMP('2024-12-31T23:59:59Z'))",
        "Compound temporal expression with AND"
    ),
    (
        "compound_temporal_or",
        "T_DURING(event_time, INTERVAL('2024-Q1-00:00:00Z', '2024-Q1-23:59:59Z')) OR T_DURING(event_time, INTERVAL('2024-Q4-00:00:00Z', '2024-Q4-23:59:59Z'))",
        "Compound temporal expression with OR"
    ),
    (
        "nested_temporal_functions",
        "T_CONTAINS(project_timeline, T_OVERLAPS(milestone_period, development_phase))",
        "Nested temporal functions"
    ),
    (
        "temporal_with_spatial",
        "T_DURING(event_time, work_hours) AND S_INTERSECTS(event_location, POLYGON((0 0, 10 0, 10 10, 0 10, 0 0)))",
        "Temporal combined with spatial predicates"
    ),
]

# Function-based temporal expressions using enhanced capabilities
TEMPORAL_FUNCTION_EXPRESSIONS: list[tuple[str, str, str]] = [
    (
        "year_extraction",
        "YEAR(created_at) = 2024",
        "Year extraction function"
    ),
    (
        "month_range",
        "MONTH(event_date) BETWEEN 6 AND 8",
        "Month extraction for summer months"
    ),
    (
        "day_of_month",
        "DAY(deadline) <= 15",
        "Day extraction for first half of month"
    ),
    (
        "hour_business",
        "HOUR(timestamp_field) BETWEEN 9 AND 17",
        "Hour extraction for business hours"
    ),
    (
        "minute_precision",
        "MINUTE(meeting_time) = 30",
        "Minute extraction for half-hour meetings"
    ),
    (
        "current_timestamp",
        "T_BEFORE(expiry_date, NOW())",
        "Current timestamp comparison"
    ),
]

# All temporal operator test cases combined
ALL_TEMPORAL_COMPLIANCE_CASES = (
    ENHANCED_TEMPORAL_OPERATORS +
    COMPLEX_TEMPORAL_EXPRESSIONS +
    TEMPORAL_FUNCTION_EXPRESSIONS
)