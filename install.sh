#!/bin/sh
# Installs the toobusy binary of this machine from a GitHub release, and replaces the one that is there.
# usage: curl -fsSL https://raw.githubusercontent.com/bitpatch/toobusy/main/install.sh | sh
# TOOBUSY_VERSION installs a given version instead of the latest, and TOOBUSY_INSTALL_DIR into another folder.
set -eu

releases="https://github.com/bitpatch/toobusy/releases"
tmp=""

die() {
  echo "toobusy: $1" >&2
  exit 1
}

need() {
  command -v "$1" >/dev/null 2>&1 || die "this script needs $1, which is not installed."
}

platform() {
  system=$(uname -s)
  machine=$(uname -m)
  case "$system" in
    Darwin) os=macos ;;
    Linux) os=linux ;;
    MINGW* | MSYS* | CYGWIN* | Windows_NT) die "there is no binary for Windows yet." ;;
    *) die "there is no binary for $system." ;;
  esac
  case "$machine" in
    x86_64 | amd64) arch=x64 ;;
    arm64 | aarch64) arch=arm64 ;;
    *) die "there is no binary for $system on $machine." ;;
  esac
  echo "$os-$arch"
}

sha256() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{ print $1 }'
  else
    sha256sum "$1" | awk '{ print $1 }'
  fi
}

download() {
  curl -fsSL "$base/$1" -o "$tmp/$1" || die "could not download $base/$1."
}

verify() {
  expected=$(awk -v name="$1" '$2 == name { print $1 }' "$tmp/SHA256SUMS")
  [ -n "$expected" ] || die "SHA256SUMS has no checksum of $1."
  [ "$(sha256 "$tmp/$1")" = "$expected" ] || die "the checksum of $1 does not match, so nothing was installed."
}

advise_path() {
  line="export PATH=\"$dir:\$PATH\""
  case "$(basename "${SHELL:-sh}")" in
    zsh) profile="$HOME/.zshrc" ;;
    bash) if [ "$(uname -s)" = Darwin ]; then profile="$HOME/.bash_profile"; else profile="$HOME/.bashrc"; fi ;;
    fish)
      profile="$HOME/.config/fish/config.fish"
      line="fish_add_path \"$dir\""
      ;;
    *) profile="$HOME/.profile" ;;
  esac
  echo "$dir is not on your PATH. Add this line to $profile and open a new terminal:"
  echo "  $line"
}

main() {
  name="toobusy-$(platform).tar.gz"
  need curl
  need tar
  command -v shasum >/dev/null 2>&1 || need sha256sum

  version=${TOOBUSY_VERSION:-}
  version=${version#v}
  if [ -n "$version" ]; then
    base="$releases/download/v$version"
  else
    base="$releases/latest/download"
  fi

  dir=${TOOBUSY_INSTALL_DIR:-$HOME/.local/bin}
  dir=${dir%/}
  mkdir -p "$dir" || die "could not make the folder $dir."

  # In the same folder as the binary, so that the move over it replaces it at once, even while it runs.
  tmp=$(mktemp -d "$dir/.toobusy-install.XXXXXX") || die "could not write into the folder $dir."
  trap 'rm -rf "$tmp"' EXIT
  trap 'exit 1' HUP INT TERM

  download SHA256SUMS
  download "$name"
  verify "$name"
  tar -xzf "$tmp/$name" -C "$tmp" toobusy || die "could not unpack $name."

  installed=$("$tmp/toobusy" --version 2>/dev/null) ||
    die "the binary does not run on this machine, so nothing was installed. On Linux it needs glibc 2.34 or newer."
  mv -f "$tmp/toobusy" "$dir/toobusy"
  echo "toobusy $installed is installed in $dir."

  case ":$PATH:" in
    *":$dir:"*) ;;
    *) advise_path ;;
  esac
}

main
