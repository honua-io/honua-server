const { test } = require("node:test");
const assert = require("node:assert/strict");
const { getDoctorPushDeferral } = require("./doctor-push-guard.cjs");

const repo = { owner: "honua-io", repo: "honua-server" };
function client(runs, head = "head") {
  return {
    rest: {
      pulls: { get: async () => ({ data: {
        state: "open", head: { sha: head, repo: { full_name: "honua-io/honua-server" } },
      } }) },
      actions: { listWorkflowRuns: "list-runs" },
    },
    paginate: async (endpoint, args) => {
      assert.equal(endpoint, "list-runs");
      assert.equal(args.workflow_id, "pr-gate.yml");
      assert.equal(args.head_sha, "head");
      return runs;
    },
  };
}

test("all unfinished PR Gate states defer the doctor", async () => {
  for (const status of ["queued", "in_progress", "waiting", "pending", "requested"]) {
    const reason = await getDoctorPushDeferral(client([{ id: 42, status }]), { repo, pullNumber: 1, expectedHead: "head" });
    assert.match(reason, new RegExp(`PR Gate run 42 is ${status}`));
  }
});

test("completed gates and a conflicting head with no gate permit a push", async () => {
  for (const runs of [[], [{ status: "completed", conclusion: "failure" }]]) {
    assert.equal(await getDoctorPushDeferral(client(runs), { repo, pullNumber: 1, expectedHead: "head" }), null);
  }
});

test("head movement between the early check and publication defers", async () => {
  assert.match(await getDoctorPushDeferral(client([], "new-head"), { repo, pullNumber: 1, expectedHead: "head" }), /head changed/);
});

test("an unreadable gate fails closed", async () => {
  const github = client([]);
  github.paginate = async () => { throw { status: 403 }; };
  assert.match(await getDoctorPushDeferral(github, { repo, pullNumber: 1, expectedHead: "head" }), /unable to verify PR Gate \(403\)/);
});

test("draft, closed and fork pull requests cannot receive a doctor push", async () => {
  for (const pr of [
    { state: "open", draft: true },
    { state: "closed" },
    { state: "open", head: { repo: { full_name: "fork/repo" } } },
  ]) {
    const github = client([]);
    github.rest.pulls.get = async () => ({ data: pr });
    assert.match(await getDoctorPushDeferral(github, { repo, pullNumber: 1, expectedHead: "head" }), /draft, closed or belongs to a fork/);
  }
});
