#!/bin/sh
# Prints the Homebrew formula of a release, from its version and the SHA256SUMS it published.
# usage: tools/write-formula.sh 1.0.0 SHA256SUMS > Formula/toobusy.rb
set -eu

version=$1
sums=$2
base="https://github.com/bitpatch/toobusy/releases/download/v$version"

checksum() {
  found=$(awk -v name="toobusy-$1.tar.gz" '$2 == name { print $1 }' "$sums")
  if [ -z "$found" ]; then
    echo "$sums has no checksum of toobusy-$1.tar.gz." >&2
    exit 1
  fi
  echo "$found"
}

macos_arm64=$(checksum macos-arm64)
macos_x64=$(checksum macos-x64)
linux_arm64=$(checksum linux-arm64)
linux_x64=$(checksum linux-x64)

cat <<EOF
class Toobusy < Formula
  desc "Hands the tasks of a project's tracker to AI coding assistants"
  homepage "https://github.com/bitpatch/toobusy"
  license "MIT"

  on_macos do
    on_arm do
      url "$base/toobusy-macos-arm64.tar.gz"
      sha256 "$macos_arm64"
    end
    on_intel do
      url "$base/toobusy-macos-x64.tar.gz"
      sha256 "$macos_x64"
    end
  end

  on_linux do
    on_arm do
      url "$base/toobusy-linux-arm64.tar.gz"
      sha256 "$linux_arm64"
    end
    on_intel do
      url "$base/toobusy-linux-x64.tar.gz"
      sha256 "$linux_x64"
    end
  end

  def install
    bin.install "toobusy"
  end

  test do
    assert_equal version.to_s, shell_output("#{bin}/toobusy --version").strip
  end
end
EOF
