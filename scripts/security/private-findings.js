const DEADLINE_DAYS = { HIGH: 30, CRITICAL: 7 };

function dateOnly(value) {
  const day = String(value || '').slice(0, 10);
  const parsed = new Date(`${day}T00:00:00Z`);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day) || !Number.isFinite(parsed.getTime()) ||
      parsed.toISOString().slice(0, 10) !== day) {
    throw new Error('Security finding requires a valid detection date');
  }
  return day;
}

function findingBody(finding, previous, run) {
  const body = previous?.body || '';
  const recorded = body.match(/^First detected: (\d{4}-\d{2}-\d{2})$/m)?.[1];
  const first = dateOnly(recorded || previous?.created_at || finding.firstDetected);
  const oldSeverity = body.match(/^Advisory: .+ \((HIGH|CRITICAL)\)$/m)?.[1];
  if (!(Object.hasOwn(DEADLINE_DAYS, finding.Severity))) throw new Error('Unsupported finding severity');
  const severity = oldSeverity === 'CRITICAL' ? oldSeverity : finding.Severity;
  const deadline = new Date(`${first}T00:00:00Z`);
  deadline.setUTCDate(deadline.getUTCDate() + DEADLINE_DAYS[severity]);
  const oldDue = body.match(/^Fix due by: (\d{4}-\d{2}-\d{2})\b/m)?.[1];
  const due = [deadline.toISOString().slice(0, 10), dateOnly(finding.dueBy),
    ...(oldDue ? [dateOnly(oldDue)] : [])].sort()[0];
  const advisory = `Advisory: ${finding.VulnerabilityID} (${severity})`;
  const dueLine = `Fix due by: ${due} (SECURITY.md: critical 7 days, high 30 days from detection)`;
  if (previous) {
    if (!oldSeverity || !oldDue) throw new Error('Existing finding has no recognized severity or deadline');
    const updated = body.replace(/^Advisory: .+$/m, advisory).replace(/^Fix due by: .+$/m, dueLine);
    return recorded ? updated : `${updated}\nFirst detected: ${first}`;
  }
  return [
    `Release: ${finding.release}`, `Image: ${finding.image}`, `Platform: ${finding.platform}`,
    advisory, `Package: ${finding.PkgName} ${finding.InstalledVersion}, fixed in ${finding.FixedVersion}`,
    `First detected: ${first}`, dueLine, `Detected by: ${run}`, '',
    'Close by shipping a patch release (docs/internal/security/security-patch-releases.md) or by recording a reasoned not_affected decision in eng/security/vex-statements.json.',
  ].join('\n');
}

module.exports = async function reportPrivateFindings({ github, env, readFileSync }) {
  const destination = env.SECURITY_FINDINGS_REPO || '';
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(destination)) {
    throw new Error('Security findings require an owner/repository destination');
  }
  const [owner, repo] = destination.split('/');
  const { data: repository } = await github.rest.repos.get({ owner, repo });
  if (repository.private !== true) throw new Error('Security findings require a private destination repository');
  const { findings } = JSON.parse(readFileSync('release-private.json', 'utf8'));
  const run = `${env.GITHUB_SERVER_URL}/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}`;
  const open = await github.paginate(github.rest.issues.listForRepo, { owner, repo, state: 'open', per_page: 100 });
  for (const finding of findings) {
    const title = `[release image] ${finding.release} ${finding.platform}: ${finding.VulnerabilityID} in ${finding.PkgName}`;
    const previous = open.find(issue => issue.title === title);
    const body = findingBody(finding, previous, run);
    if (previous) {
      if (body !== previous.body) {
        await github.rest.issues.update({ owner, repo, issue_number: previous.number, body });
        previous.body = body;
      }
    } else {
      const { data: created } = await github.rest.issues.create({ owner, repo, title, body });
      open.push(created);
    }
  }
};
