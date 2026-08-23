# Security Policy

We take security seriously at Cordango, and we appreciate the effort it takes to find and report a
vulnerability responsibly.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Email **[hello@cordango.com](mailto:hello@cordango.com)** with the subject line `SECURITY`, or use
GitHub's [private vulnerability reporting](https://github.com/cordango/cordango/security/advisories/new)
on this repository.

### What to include

1. **Description** — what the vulnerability is
2. **Impact** — what somebody could achieve by exploiting it
3. **Steps to reproduce** — enough for us to see it happen
4. **Affected versions** — which release, if you know
5. **Suggested fix** — optional, and welcome

### What to expect

- **Acknowledgement** within 48 hours
- **Updates** as we investigate
- **Resolution** — we aim to fix critical issues within 90 days, and to credit you in the advisory
  unless you would rather we did not

All reports are kept confidential. We will not share your details with anyone without your consent,
except where the law requires it.

## Supported versions

Pre-alpha: only the latest release is supported. There are no backports, and the version you should
be running is the newest one.

## Scope

This policy covers:

- The `cordango` command line and the packages published from this repository
  (`Cordango.Cli`, `Cordango.Standalone`)
- **The code this toolchain generates.** A flaw in a generated application's authentication,
  permission enforcement or data access is a flaw in the generator, and it is the most serious kind
  of report we can get: it is reproduced in every application anybody has generated.
- This repository, `cordango/examples`, `cordango/homebrew-tap` and `cordango/scoop-bucket`

### Out of scope

- **Applications you have generated and then changed.** Once you edit the output it is your code —
  though if you can show the same flaw reproducing from a clean `cordango build`, that is in scope
  and we want it.
- Vulnerabilities in dependencies, unless this project's use of them is what makes them exploitable.
  Report those upstream; tell us anyway if we should pin or patch.
- Anything requiring an attacker to already have write access to the machine running `cordango`, or
  to the definition being compiled. A definition is source code and is trusted like source code.
- Missing hardening headers, rate limits or similar on a locally-run development container.

## Cordango Platform

The hosted product is not in this repository. For a vulnerability in Cordango Platform, email the
same address — say which one you mean, and we will route it.
