#!/usr/bin/env bash
#
# build-base-fs.sh — assemble the HCore base filesystem release artifact.
#
# The kernel repo ships NO filesystem. This script materialises a base FS
# (rootfs) from the essential packages listed in the kernel's bootstrap.json,
# downloading each package's .hpk release and unpacking it with the same
# semantics as `hpm install`. The result is tarred into hcore-base-fs.tar.gz,
# suitable for upload as a GitHub Release asset alongside the kernel binary.
#
# Usage:
#   scripts/build-base-fs.sh [-o OUTPUT.tar.gz] [-w WORKDIR] [--no-tar]
#
# Environment:
#   HPK_DIR    If set, .hpk files are taken from this local directory
#              (named <PackageName>-<version>.hpk) instead of being downloaded.
#              Useful for offline/dev builds and CI that already built the packs.
#
# Dependencies: bash, jq, tar, gzip, sha256sum, and curl (unless HPK_DIR is set).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BOOTSTRAP_JSON="${BOOTSTRAP_JSON:-$REPO_ROOT/src/HCore.Main/Bootstrap/bootstrap.json}"

OUTPUT="$REPO_ROOT/hcore-base-fs.tar.gz"
WORKDIR=""
DO_TAR=1

die() { echo "error: $*" >&2; exit 1; }
info() { echo ">> $*" >&2; }

while [ $# -gt 0 ]; do
  case "$1" in
    -o|--output) OUTPUT="$2"; shift 2 ;;
    -w|--workdir) WORKDIR="$2"; shift 2 ;;
    --no-tar) DO_TAR=0; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

command -v jq >/dev/null 2>&1 || die "jq is required"
[ -f "$BOOTSTRAP_JSON" ] || die "bootstrap.json not found at $BOOTSTRAP_JSON"

if [ -z "$WORKDIR" ]; then
  WORKDIR="$(mktemp -d)"
  trap 'rm -rf "$WORKDIR"' EXIT
fi

STAGING="$WORKDIR/FS"
DOWNLOADS="$WORKDIR/hpk"
mkdir -p "$STAGING/packs" "$STAGING/etc/services" "$STAGING/home" "$STAGING/data" "$DOWNLOADS"

# Fetch a package's .hpk into $DOWNLOADS and echo its path.
fetch_hpk() {
  name="$1"; version="$2"; url="$3"
  local_file="$DOWNLOADS/${name}-${version}.hpk"

  if [ -n "${HPK_DIR:-}" ]; then
    src="$HPK_DIR/${name}-${version}.hpk"
    [ -f "$src" ] || die "HPK_DIR set but $src not found"
    cp "$src" "$local_file"
  else
    [ "$url" != "null" ] && [ -n "$url" ] || die "$name has no url and HPK_DIR is unset"
    info "downloading $name v$version"
    curl -fsSL "$url" -o "$local_file" || die "download failed: $url"
  fi
  echo "$local_file"
}

# Unpack one .hpk into the staging FS. Handles both .hpk layouts:
#   flat (files at archive root) and structured (lib/, etc/, src/, scripts/).
#   root-level files -> FS/packs/<name>/   (dll, mpd, manifest.json, deps.json)
#   lib/*            -> FS/packs/<name>/    (flattened, structured layout)
#   etc/*            -> merged into FS/etc/ (overlay, if present)
#   src/, scripts/   -> ignored for a base image.
# This mirrors the kernel's HpkArchive.Extract so the shipped base FS matches
# what the kernel would self-heal to over the network.
install_hpk() {
  name="$1"; hpk="$2"; sha="$3"

  if [ "$sha" != "null" ] && [ -n "$sha" ]; then
    actual="$(sha256sum "$hpk" | cut -d' ' -f1)"
    [ "$actual" = "$sha" ] || die "$name checksum mismatch (expected $sha, got $actual)"
  fi

  extract="$WORKDIR/extract/$name"
  rm -rf "$extract"; mkdir -p "$extract"
  tar -xzf "$hpk" -C "$extract"

  packdir="$STAGING/packs/$name"
  mkdir -p "$packdir"

  find "$extract" -maxdepth 1 -type f -exec cp {} "$packdir/" \;
  [ -d "$extract/lib" ] && cp -a "$extract/lib/." "$packdir/"
  [ -d "$extract/etc" ] && cp -a "$extract/etc/." "$STAGING/etc/"

  [ -f "$packdir/mpd" ] || info "warning: $name has no mpd descriptor — kernel will skip it"
}

count="$(jq '.essential | length' "$BOOTSTRAP_JSON")"
[ "$count" -gt 0 ] || die "no essential packages listed in bootstrap.json"
info "assembling base FS from $count essential package(s)"

i=0
while [ "$i" -lt "$count" ]; do
  name="$(jq -r ".essential[$i].name"    "$BOOTSTRAP_JSON")"
  version="$(jq -r ".essential[$i].version" "$BOOTSTRAP_JSON")"
  url="$(jq -r ".essential[$i].url // \"null\""    "$BOOTSTRAP_JSON")"
  sha="$(jq -r ".essential[$i].sha256 // \"null\"" "$BOOTSTRAP_JSON")"

  hpk="$(fetch_hpk "$name" "$version" "$url")"
  install_hpk "$name" "$hpk" "$sha"
  info "installed $name"
  i=$((i + 1))
done

if [ "$DO_TAR" -eq 1 ]; then
  info "packing $OUTPUT"
  tar -czf "$OUTPUT" -C "$WORKDIR" FS
  info "done: $OUTPUT ($(du -h "$OUTPUT" | cut -f1))"
else
  info "staging FS ready at $STAGING (--no-tar)"
fi
