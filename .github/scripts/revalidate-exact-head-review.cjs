// Shared fail-closed review revalidation; the aggregate required gate checks the ready PR head.
module.exports = async ({ github, context, core, expectedHead }) => {
  const head = context.payload.pull_request?.head?.sha;
  if (!head || head !== expectedHead) {
    core.setFailed('No trustworthy pull-request head for final review validation.');
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
