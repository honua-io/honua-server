// Shared fail-closed review revalidation; callers preserve attempt-2 admission.
module.exports = async ({ github, context, core, expectedHead }) => {
  const head = context.payload.pull_request?.head?.sha;
  if (!head || head !== expectedHead) {
    core.setFailed('Attempt 2 has no trustworthy pull-request head.');
    return;
  }
  const statuses = await github.paginate(
    github.rest.repos.listCommitStatusesForRef,
    { ...context.repo, ref: head, per_page: 100 },
  );
  const reviewGate = statuses.find(status => status.context === 'Review Gate');
  if (reviewGate?.state !== 'success') {
    core.setFailed(`Exact-head Review Gate is ${reviewGate?.state || 'missing'}.`);
  }
};
