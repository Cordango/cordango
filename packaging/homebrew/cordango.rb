# The Cordango command line, as a Homebrew formula.
#
# THIS FILE BELONGS IN `cordango/homebrew-tap`, at `Formula/cordango.rb`. It is kept here so the
# canonical copy sits beside the workflow that produces the assets it points at; copy it out on
# release, with the version and the four checksums updated from the release's SHA256SUMS.
#
# A FORMULA RATHER THAN A CASK, deliberately. A cask is for .app bundles and installers, and Homebrew
# quarantines what a cask downloads — which is why unsigned casks make people run `brew trust` before
# anything will start. A formula extracts a tarball into the Cellar and is not quarantined, so an
# unsigned binary installed this way simply runs. That is what lets this ship before there is an
# Apple Developer ID to sign with.
class Cordango < Formula
  desc "Compile an App Definition into a complete application you own"
  homepage "https://github.com/cordango/cordango"
  version "0.4.0"
  license "Apache-2.0"

  # No `depends_on`. The binary is self-contained: it carries its own .NET runtime and needs no
  # SDK, no ICU, and nothing else on the machine.
  on_macos do
    on_arm do
      url "https://github.com/cordango/cordango/releases/download/v#{version}/cordango-#{version}-osx-arm64.tar.gz"
      sha256 "c7e39139a7da5636d7ad3eaf77c8dafa0d6fe1bca32407e739ee4220750dcb8d"
    end
    on_intel do
      url "https://github.com/cordango/cordango/releases/download/v#{version}/cordango-#{version}-osx-x64.tar.gz"
      sha256 "2a63088efab72780f1d5f752273f010696a3f203f89e62e4ef9006ee2462567f"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/cordango/cordango/releases/download/v#{version}/cordango-#{version}-linux-arm64.tar.gz"
      sha256 "27dda2b27543ac9cf50db544dc4f57ef7f0b7643b8b301df9d000bb482aa5c40"
    end
    on_intel do
      url "https://github.com/cordango/cordango/releases/download/v#{version}/cordango-#{version}-linux-x64.tar.gz"
      sha256 "7fdc524f8838ef142ca1f4de62543b79465be3bd654e635d7eb233ccb3305855"
    end
  end

  def install
    bin.install "cordango"
  end

  # `brew test` runs this. Deliberately a command that exercises the embedded resources rather than
  # just printing a string: `version` reports the App Definition schema version, which means the
  # schema was found inside the single-file bundle. A binary that started but could not read its own
  # resources would pass a plainer test and fail on the user's first real command.
  test do
    assert_match "App Definition schema", shell_output("#{bin}/cordango version")
  end
end
