#!/bin/sh
# Point the Homebrew formula and the Scoop manifest at a release.
#
#     packaging/render.sh 0.5.1-alpha assets/SHA256SUMS
#
# Both files carry a version and the SHA-256 of every asset they download, so both are wrong the
# moment a release is cut and stay wrong until somebody edits them. The checksums cannot be written
# in advance — they are of files that do not exist yet — so the honest order is: build the release,
# then render these from the SHA256SUMS it produced. release.yml does exactly that and attaches the
# results, which is why the copies checked in here carry zeroed hashes: they are the shape, not the
# answer. Take the rendered pair off the release and put it in the tap and the bucket.
#
# Run it by hand for an older release with:
#
#     curl -fsSLO https://github.com/cordango/cordango/releases/download/v0.5.1-alpha/SHA256SUMS
#     packaging/render.sh 0.5.1-alpha SHA256SUMS
set -eu

version="${1:?usage: render.sh <version> <SHA256SUMS>}"
sums="${2:?usage: render.sh <version> <SHA256SUMS>}"

[ -f "$sums" ] || { echo "render.sh: no such file: $sums" >&2; exit 1; }

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# A missing line is a hard failure rather than an empty hash. An empty hash renders a manifest that
# looks finished and rejects every download, which is the worst of the three outcomes.
hash_for() {
    name="cordango-$version-$1"
    found=$(awk -v n="$name" '$2 == n { print $1 }' "$sums")
    [ -n "$found" ] || { echo "render.sh: $sums names no $name" >&2; exit 1; }
    echo "$found"
}

# The formula builds its own URLs out of `version`, so only the version and the four checksums move.
awk -v version="$version" \
    -v osx_arm64="$(hash_for osx-arm64.tar.gz)" \
    -v osx_x64="$(hash_for osx-x64.tar.gz)" \
    -v linux_arm64="$(hash_for linux-arm64.tar.gz)" \
    -v linux_x64="$(hash_for linux-x64.tar.gz)" '
    BEGIN {
        hash["osx-arm64"] = osx_arm64
        hash["osx-x64"] = osx_x64
        hash["linux-arm64"] = linux_arm64
        hash["linux-x64"] = linux_x64
    }
    /^  version "/ { print "  version \"" version "\""; next }
    /url .*osx-arm64/ { rid = "osx-arm64" }
    /url .*osx-x64/ { rid = "osx-x64" }
    /url .*linux-arm64/ { rid = "linux-arm64" }
    /url .*linux-x64/ { rid = "linux-x64" }
    /^ *sha256 "/ && rid != "" { sub(/"[0-9a-f]*"/, "\"" hash[rid] "\"") }
    { print }
' "$here/homebrew/cordango.rb" > "$here/homebrew/cordango.rb.rendered"
mv "$here/homebrew/cordango.rb.rendered" "$here/homebrew/cordango.rb"

# The manifest spells its URLs out, so they are rebuilt rather than patched — a substitution over a
# version that itself contains a hyphen is how `0.5.0-alpha` becomes `0.5.0-alpha-win-x64` twice.
awk -v version="$version" \
    -v win_x64="$(hash_for win-x64.zip)" \
    -v win_arm64="$(hash_for win-arm64.zip)" '
    BEGIN {
        hash["win-x64"] = win_x64
        hash["win-arm64"] = win_arm64
        base = "https://github.com/cordango/cordango/releases/download"
    }
    /^    "version":/ { print "    \"version\": \"" version "\","; next }
    /"url": .*win-arm64/ { rid = "win-arm64" }
    /"url": .*win-x64\.zip/ { rid = "win-x64" }
    /"url": .*releases\/download/ && rid != "" && $0 !~ /\$version/ {
        match($0, /^ */)
        printf "%s\"url\": \"%s/v%s/cordango-%s-%s.zip\",\n", substr($0, 1, RLENGTH), base, version, version, rid
        next
    }
    /"hash":/ && rid != "" { sub(/"[0-9a-f]*"$/, "\"" hash[rid] "\"") }
    { print }
' "$here/scoop/cordango.json" > "$here/scoop/cordango.json.rendered"
mv "$here/scoop/cordango.json.rendered" "$here/scoop/cordango.json"

echo "rendered packaging for $version"
