#!/usr/bin/env bash
#
# build-base-fs-from-source.sh — build a bootable HCore base filesystem entirely
# from source: clone each base package from GitHub, build it, and assemble the
# result into <FS_DIR>/packs/. No prebuilt FS, no released .hpk needed.
#
# (Companion to build-base-fs.sh, which assembles from published .hpk release
# assets — use that once packages publish releases; use THIS one meanwhile.)
#
# Usage:
#   scripts/build-base-fs-from-source.sh [-o FS_DIR] [-w WORKDIR] [--with-kernel] [--tar]
#
# Options:
#   -o FS_DIR       Output filesystem root (default: <repo>/FS)
#   -w WORKDIR      Where package repos are cloned/built (default: mktemp)
#   --with-kernel   Also build the kernel (HCore.Main) in this repo
#   --tar           Also produce hcore-base-fs.tar.gz next to FS_DIR
#
# Requires: .NET 10 SDK, git.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
GH="https://github.com/Ardumine"

# The packages that make up a bootable base FS. Repo names on GitHub; each is
# self-contained via submodules (Base; Nexus also bundles KASerializer). These
# are the kernel's bootstrap.json essentials PLUS Nexus, which the kernel spawns
# at boot (Program.cs) and so must be present for the FS to boot.
REPOS=(hinit hshell hpm hshellutils hshellnetutils nexus)

FS_DIR="$REPO_ROOT/FS"
WORKDIR=""
WITH_KERNEL=0
DO_TAR=0

info() { echo ">> $*" >&2; }
die()  { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    -o|--output) FS_DIR="$2"; shift 2 ;;
    -w|--workdir) WORKDIR="$2"; shift 2 ;;
    --with-kernel) WITH_KERNEL=1; shift ;;
    --tar) DO_TAR=1; shift ;;
    -h|--help) sed -n '2,26p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

command -v dotnet >/dev/null 2>&1 || die "dotnet (.NET 10 SDK) is required"
command -v git    >/dev/null 2>&1 || die "git is required"

if [ -z "$WORKDIR" ]; then
  WORKDIR="$(mktemp -d)"
  trap 'rm -rf "$WORKDIR"' EXIT
fi
mkdir -p "$WORKDIR" "$FS_DIR/packs" "$FS_DIR/etc/services"

# Build one package repo and copy its output into the FS as a pack.
build_pkg() {
  repo="$1"
  src="$WORKDIR/$repo"
  out="$WORKDIR/out/$repo"

  if [ -d "$src/.git" ]; then
    info "$repo: updating"
    git -C "$src" pull --ff-only --recurse-submodules -q || true
    git -C "$src" submodule update --init --recursive -q
  else
    info "$repo: cloning"
    git clone --recurse-submodules -q "$GH/$repo.git" "$src"
  fi

  info "$repo: building"
  dotnet build "$src" -c Release -o "$out" -v quiet --nologo >/dev/null

  # The pack directory name is the assembly name = line 1 of mpd minus .dll.
  [ -f "$src/mpd" ] || die "$repo: no mpd file"
  pack="$(head -n1 "$src/mpd" | tr -d '\r' | sed 's/\.dll$//')"
  dest="$FS_DIR/packs/$pack"

  info "$repo -> packs/$pack"
  rm -rf "$dest"; mkdir -p "$dest"
  cp -a "$out/." "$dest/"

  # Merge an etc/ overlay if the package ships one (services, config).
  if [ -d "$src/etc" ]; then
    cp -a "$src/etc/." "$FS_DIR/etc/"
  fi
}

for r in "${REPOS[@]}"; do
  build_pkg "$r"
done

if [ "$WITH_KERNEL" -eq 1 ]; then
  info "building kernel (HCore.Main)"
  dotnet build "$REPO_ROOT/HCore.Main/HCore.Main.csproj" -c Release -v quiet --nologo >/dev/null
fi

info "base FS assembled at: $FS_DIR"
info "packs: $(ls "$FS_DIR/packs" | tr '\n' ' ')"

if [ "$DO_TAR" -eq 1 ]; then
  tarball="$(dirname "$FS_DIR")/hcore-base-fs.tar.gz"
  info "packing $tarball"
  tar -czf "$tarball" -C "$(dirname "$FS_DIR")" "$(basename "$FS_DIR")"
  info "done: $tarball"
fi

echo
echo "Run the kernel against it:"
echo "  dotnet run --project \"$REPO_ROOT/HCore.Main\" -- --fs=\"$FS_DIR\""
