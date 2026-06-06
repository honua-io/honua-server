-- Migration: 050_CreateFieldReview.sql
-- Back-office field data review & QA over mobile-submitted form records (#1159).
-- Server-owned review state, assignment, and comments/correction requests layered
-- over the existing honua.form_submissions table without mutating submissions.

CREATE SCHEMA IF NOT EXISTS honua;

-- One review-state row per submission. Created lazily on the first review action.
CREATE TABLE IF NOT EXISTS honua.field_submission_reviews (
    submission_id  UUID        NOT NULL PRIMARY KEY
        REFERENCES honua.form_submissions(submission_id) ON DELETE CASCADE,
    status         TEXT        NOT NULL DEFAULT 'pending',
    assigned_to    TEXT,
    decided_by     TEXT,
    decided_at     TIMESTAMPTZ,
    decision_note  TEXT,
    etag           TEXT        NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    has_conflict   BOOLEAN     NOT NULL DEFAULT false,
    CONSTRAINT field_submission_reviews_valid_status
        CHECK (status IN ('pending', 'in_review', 'changes_requested', 'approved', 'rejected'))
);

CREATE INDEX IF NOT EXISTS idx_field_submission_reviews_status
    ON honua.field_submission_reviews(status);

CREATE INDEX IF NOT EXISTS idx_field_submission_reviews_assigned
    ON honua.field_submission_reviews(assigned_to)
    WHERE assigned_to IS NOT NULL;

-- Reviewer comments and correction requests attached to a submission.
CREATE TABLE IF NOT EXISTS honua.field_review_comments (
    comment_id         UUID        NOT NULL PRIMARY KEY,
    submission_id      UUID        NOT NULL
        REFERENCES honua.form_submissions(submission_id) ON DELETE CASCADE,
    author             TEXT        NOT NULL,
    body               TEXT        NOT NULL,
    correction_request BOOLEAN     NOT NULL DEFAULT false,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_field_review_comments_submission
    ON honua.field_review_comments(submission_id, created_at);
