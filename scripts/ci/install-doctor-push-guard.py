"""Install the publication guard into the pinned gh-aw v1.0.1 runtime; fail closed on drift."""

from pathlib import Path
import shutil
import sys


def install_guard(path: Path) -> None:
    source = path.read_text()
    if "getDoctorPushDeferral" in source:
        raise RuntimeError("Doctor push guard already installed")
    replacements = {
        '    let pushRepo = itemRepo;': '''    const { getDoctorPushDeferral } = require("./doctor-push-guard.cjs");
    const doctorGateClient = global.getOctokit(process.env.DOCTOR_GATE_READ_TOKEN);
    const doctorHead = pullRequest.head.sha;
    const doctorDeferral = await getDoctorPushDeferral(doctorGateClient, { repo: repoParts, pullNumber, expectedHead: doctorHead });
    if (doctorDeferral) {
      core.info(doctorDeferral);
      return { skipped: true, reason: doctorDeferral };
    }
    let pushRepo = itemRepo;''',
        '          return pushSignedCommits({': '''          const doctorDeferral = await getDoctorPushDeferral(doctorGateClient, { repo: repoParts, pullNumber, expectedHead: doctorHead });
          if (doctorDeferral) throw new StalePullRequestError(doctorDeferral);
          return pushSignedCommits({''',
    }
    for original, replacement in replacements.items():
        if source.count(original) != 1:
            raise RuntimeError(f"Unexpected gh-aw push runtime: {original.strip()}")
        source = source.replace(original, replacement)
    shutil.copyfile(Path(__file__).with_name("doctor-push-guard.cjs"), path.with_name("doctor-push-guard.cjs"))
    path.write_text(source)


if __name__ == "__main__":
    install_guard(Path(sys.argv[1]))
