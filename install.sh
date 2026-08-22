#!/bin/sh
#
# Install the Cordango command line.
#
#     curl -fsSL https://cordango.com/install.sh | sh
#
# Or, for a specific version or somewhere other than ~/.local/bin:
#
#     curl -fsSL https://cordango.com/install.sh | sh -s -- --version 0.4.0
#     curl -fsSL https://cordango.com/install.sh | CORDANGO_INSTALL_DIR=/usr/local/bin sh
#
# What it does: works out which binary this machine wants, downloads it from the GitHub release,
# CHECKS THE SHA-256 against the checksum file published beside it, and puts one file on disk. It
# writes nothing else, touches no shell profile, and needs no .NET — the binary carries its own
# runtime. Uninstalling is deleting that file.
#
# POSIX sh on purpose. This is the first thing a person runs, and "bash: not found" on a container
# they were trying Cordango in is a bad introduction.

set -eu

REPO="cordango/cordango"
VERSION="latest"
INSTALL_DIR="${CORDANGO_INSTALL_DIR:-$HOME/.local/bin}"

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:?--version needs a version, for example 0.4.0}"; shift 2 ;;
        --dir)     INSTALL_DIR="${2:?--dir needs a directory}"; shift 2 ;;
        -h|--help)
            sed -n '3,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "cordango: unknown option '$1'" >&2; exit 2 ;;
    esac
done

fail() { echo "cordango: $*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || fail "this needs '$1' and could not find it."; }

need uname
need tar

# curl or wget, whichever is here. Minimal images tend to have exactly one.
if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL "$1" -o "$2"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -q "$1" -O "$2"; }
else
    fail "this needs curl or wget and could not find either."
fi

# ---- which binary -----------------------------------------------------------------------------

case "$(uname -s)" in
    Linux)  os="linux" ;;
    Darwin) os="osx" ;;
    *) fail "no build for $(uname -s). Windows has a Scoop package: scoop install cordango/cordango" ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *) fail "no build for $(uname -m). Supported: x86_64 and arm64." ;;
esac

rid="$os-$arch"

# ---- which version ----------------------------------------------------------------------------

if [ "$VERSION" = "latest" ]; then
    base="https://github.com/$REPO/releases/latest/download"

    # The tag, so the file name can be built and the version can be reported. Read from the redirect
    # rather than from the API: no token, no rate limit, no JSON parser to depend on.
    if command -v curl >/dev/null 2>&1; then
        tag=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest" | sed 's|.*/||')
    else
        tag=$(wget -qS --spider "https://github.com/$REPO/releases/latest" 2>&1 |
              sed -n 's|.*Location:.*/tag/\([^ ]*\).*|\1|p' | tail -1)
    fi

    [ -n "${tag:-}" ] || fail "could not work out the latest version. Pass --version explicitly."
    version="${tag#v}"
else
    version="${VERSION#v}"
    base="https://github.com/$REPO/releases/download/v$version"
fi

asset="cordango-$version-$rid.tar.gz"

# ---- download, verify, install ------------------------------------------------------------------

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

echo "cordango $version ($rid)"

fetch "$base/$asset" "$tmp/$asset" || fail "could not download $base/$asset"

# The checksum is not optional. An interrupted download is a binary that fails somewhere unrelated,
# and a tampered one is not noticed at all. If no tool can check it, say so rather than shrug.
if fetch "$base/SHA256SUMS" "$tmp/SHA256SUMS" 2>/dev/null; then
    expected=$(grep " $asset\$" "$tmp/SHA256SUMS" | cut -d' ' -f1)
    [ -n "${expected:-}" ] || fail "$asset is not listed in SHA256SUMS."

    if command -v sha256sum >/dev/null 2>&1; then
        actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
    elif command -v shasum >/dev/null 2>&1; then
        actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
    else
        fail "this needs sha256sum or shasum to verify the download."
    fi

    [ "$expected" = "$actual" ] ||
        fail "checksum mismatch for $asset. Expected $expected, got $actual. Not installing."
else
    fail "could not download the checksum file. Not installing an unverified binary."
fi

tar -xzf "$tmp/$asset" -C "$tmp"
[ -f "$tmp/cordango" ] || fail "$asset did not contain a cordango binary."

mkdir -p "$INSTALL_DIR"
chmod +x "$tmp/cordango"

# Move rather than copy over the top, so replacing a RUNNING binary works: the old inode stays alive
# for the process that has it open and the name points at the new one.
mv -f "$tmp/cordango" "$INSTALL_DIR/cordango"

echo "installed $INSTALL_DIR/cordango"

case ":$PATH:" in
    *":$INSTALL_DIR:"*)
        echo
        "$INSTALL_DIR/cordango" version ;;
    *)
        echo
        echo "$INSTALL_DIR is not on your PATH. Add it:"
        echo
        echo "    export PATH=\"\$PATH:$INSTALL_DIR\""
        echo ;;
esac
