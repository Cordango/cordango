# Packaging

Everything here describes how the released binaries reach a machine. The binaries themselves are
built by [`.github/workflows/release.yml`](../.github/workflows/release.yml) and attached to the
GitHub Release; every channel below points at those same assets.

| Channel | Who it is for |
| --- | --- |
| [`../install.sh`](../install.sh) | Linux and macOS. Needs nothing but `curl` and `tar`. |
| [`homebrew/cordango.rb`](homebrew/cordango.rb) | macOS and Linux, for people who already live in Homebrew. |
| [`scoop/cordango.json`](scoop/cordango.json) | Windows. |
| `dotnet tool install -g Cordango.Cli` | People who already have the .NET SDK. Published by [`publish.yml`](../.github/workflows/publish.yml). |

**The binary needs no .NET.** It is self-contained and compressed, about 39 MB, and carries its own
runtime. That is the point of this being the primary channel: Cordango is an application language,
and somebody generating a Go or React target should not be asked to install a .NET SDK to run the
compiler.

## The two files here live in other repositories

Homebrew and Scoop both discover packages through a repository with a fixed name, so neither file can
live in this one. They are kept here so that the canonical version is beside the workflow that
produces the assets they reference, and copied out on release.

| File | Repository | Path there |
| --- | --- | --- |
| `homebrew/cordango.rb` | `cordango/homebrew-tap` | `Formula/cordango.rb` |
| `scoop/cordango.json` | `cordango/scoop-bucket` | `bucket/cordango.json` |

Then:

```
brew install cordango/tap/cordango

scoop bucket add cordango https://github.com/cordango/scoop-bucket
scoop install cordango
```

## Updating them for a release

Both files carry the version and the SHA-256 of each asset, so both change on every release. The
checksums are in the `SHA256SUMS` file the release job publishes beside the binaries:

```
curl -fsSL https://github.com/cordango/cordango/releases/download/v0.4.0/SHA256SUMS
```

Scoop can do it by itself — the manifest carries `checkver` and `autoupdate`, and
`scoop-bucket`'s own scheduled workflow updates the version and hashes. Homebrew's formula is updated
by hand or by `brew bump-formula-pr`.

## A formula, not a cask

A cask is for `.app` bundles and installers, and Homebrew **quarantines** what a cask downloads —
which is why an unsigned cask makes people run `brew trust` before it will start. A formula extracts
a tarball into the Cellar and is not quarantined, so an unsigned binary installed this way simply
runs. That is why `cordango.rb` is a formula, and it is what lets this ship before there is an Apple
Developer ID to sign with.

The same is true of `install.sh`: a file written by `curl` never gets the quarantine attribute, so
Gatekeeper does not object. A binary downloaded in a BROWSER does, which is the one path that will
need signing and notarization when it matters.
