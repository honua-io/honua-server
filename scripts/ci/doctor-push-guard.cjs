// Read-only publication policy shared by all three doctors.
async function getDoctorPushDeferral(github, { repo, pullNumber, expectedHead }) {
  try {
    const { data: pr } = await github.rest.pulls.get({ ...repo, pull_number: pullNumber });
    if (pr.draft || pr.state !== "open" || pr.head.repo?.full_name !== `${repo.owner}/${repo.repo}`) {
      return "Doctor push deferred: pull request is draft, closed or belongs to a fork";
    }
    if (pr.head.sha !== expectedHead) {
      return "Doctor push deferred: pull request head changed during safe outputs";
    }
    const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
      ...repo,
      workflow_id: "pr-gate.yml",
      head_sha: expectedHead,
      per_page: 100,
    });
    const active = runs.find(run => run.status !== "completed");
    if (active) {
      return `Doctor push deferred: PR Gate run ${active.id} is ${active.status} on ${expectedHead}`;
    }
    return null;
  } catch (error) {
    return `Doctor push deferred: unable to verify PR Gate (${error.status || "API error"})`;
  }
}

module.exports = { getDoctorPushDeferral };
