# Review prevention analyzers

Release and CI builds treat CA2000, IDE0063, RS0030, S1244, S2486, S108,
CA1508 and CA1851 as build errors. RS0030 bans the Path.Combine overloads;
use Path.Join. The pinned Sonar analyzer enables only the three requested
Sonar rules through `scripts/ci/analyzer-baseline/sonar.globalconfig`, which also
enables the other requested rules and disables the remaining Sonar rules.
Root `Directory.Build.props` imports this policy alongside the baseline and
suppressor. Independent harnesses with their own build props retain their
separate analyzer policy; the root editor configuration does not enable
unbaselined rules in those builds. Regenerate that rule inventory from the matching
Sonar release's `analyzers/rspec/cs/S*.json` files when upgrading the package.
The analyzer dependencies are private build assets.

`scripts/ci/analyzer-baseline/AnalyzerBaseline.tsv` records existing findings by rule,
repository-relative source path, hash of the invariant diagnostic message, enclosing C# member scope
and trimmed source line, and occurrence count. The diagnostic suppressor
allows only those recorded findings. A missing/unreadable baseline, changed
message, scope or line, new file, duplicate baseline row or increased occurrence count leaves the finding
unsuppressed. Moving an unchanged source line within its enclosing scope preserves the
baseline. This avoids blanket file, project or rule exemptions. Whitespace
at the beginning/end of the line is ignored; an identical replacement of
existing debt is intentionally indistinguishable from that debt.

Paying down debt should remove its baseline row or reduce its count. Do not
automatically regenerate the baseline in CI or add rows to make a PR pass.
Any baseline increase requires explicit review of the existing debt being
recorded. Initial capture is a maintenance operation on the build host:

```sh
# Begin with a clean SARIF directory; preserve the checked-in baseline.
remote-run dotnet build Honua.sln -c Release --no-incremental -p:HonuaCaptureAnalyzerBaseline=true
remote-run dotnet run --project scripts/ci/analyzer-baseline -- \
  . artifacts/analyzer-baseline scripts/ci/analyzer-baseline/AnalyzerBaseline.tsv
# Then verify an ordinary Release build with no capture switch.
remote-run dotnet build Honua.sln -c Release
```

Run the converter on the build host if SARIF contains absolute remote paths.
Check every expected project produced a report before accepting the capture.
The capture switch keeps unrelated warnings as errors, writes one SARIF file
per project and downgrades only the requested rules. It omits the baseline from
analyzer inputs so previously recorded debt is captured again, rather than silently
dropped during recapture. It is never a CI default.

The CodeQL security workflow uses `.github/codeql/codeql-config.yml`, which
excludes `cs/linq/missed-where` and `cs/linq/missed-select`. These exclusions
do **not** configure the separately managed GitHub Code Quality commenter.
The CodeQL Action currently rejects query customizations when only
`code-quality` analysis is enabled. Keep its security checks enabled;
changing the managed commenter's behavior requires a supported GitHub
configuration rather than silently weakening security analysis.

`dotnet format` does not load the diagnostic suppressor in the current SDK
([upstream report](https://github.com/dotnet/sdk/issues/51364)). CI and pre-PR
format verification exclude these eight IDs from the formatter only. The required
analyzer build rows still enforce every ID as an error with the same exact
per-finding baseline; all other format diagnostics remain enabled.

The baseline also records eight exact CA2000 message variants from the pinned
container SDK: its message spells "transferred" where the build-host analyzer
spells "transfered". Each variant retains the same source/member identity and
occurrence budget. When refreshing with one SDK, preserve and separately verify
existing variants for the other supported SDK rather than broadening message
matching or dropping image enforcement.
