#!/usr/bin/env bash
# Secret scan before committing evidence (logs, reports, screenshots, research data).
#   1. Credential patterns in every tracked file and in the given extra paths (default: the whole repository).
#   2. The actual values of credential-like environment variables of this machine (names ending in TOKEN, KEY, SECRET,
#      PASSWORD, CREDENTIALS; values of 8+ characters), searched literally. Values are never printed: only the variable
#      name and the file that holds it.
#   3. The same values in the full git history of the current branch.
#   4. PNG text chunks (tEXt / iTXt / zTXt) in screenshots: they must carry no text at all.
# Exit code 1 when anything is found.
#
#   tools/verify/secret-scan.sh [extra paths...]
set -uo pipefail
REPO=$(git rev-parse --show-toplevel)
cd "$REPO"
found=0

echo "== 1. credential patterns (tracked files + extra paths)"
PATTERNS='(sk-[A-Za-z0-9_-]{20,}|sk-ant-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}|-----BEGIN [A-Z ]*PRIVATE KEY-----|eyJ[A-Za-z0-9_-]{20,}\.eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,})'
# Judged per match, not per file: a fixture value spells itself out (FAKE…, an alphabet run, 0123456789, xyzxyzxyz, AWS's documented …EXAMPLE key) and a
# private-key header only counts in a test file; anything else is reported.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  f=${line%%:*}; m=${line#*:}
  if [[ $m =~ [Ff][Aa][Kk][Ee]|[Aa][Bb][Cc][Dd][Ee][Ff]|0123456789|EXAMPLE$|(xyz){3} ]] || { [[ $m == -----BEGIN* ]] && [[ $f =~ (\.test\.ts|Tests\.cs)$ ]]; }; then
    echo "  fixture (fake value): $f"
  else
    echo "  PATTERN in $f: ${m:0:6}… (${#m} chars)"; found=1
  fi
done < <( { git ls-files -z | xargs -0 grep -I -o -E "$PATTERNS" 2>/dev/null; for p in "$@"; do grep -r -I -o -E "$PATTERNS" "$p" 2>/dev/null; done; } | sort -u)

echo "== 2-3. this machine's credential values (tracked files, extra paths, git history)"
names=$(env | cut -d= -f1 | grep -E '(TOKEN|KEY|SECRET|PASSWORD|CREDENTIALS)$|^(GH_TOKEN|GITHUB_TOKEN)$' | sort -u)
vals=$(mktemp); trap 'rm -f "$vals"' EXIT
for n in $names; do
  v=${!n}
  [ ${#v} -ge 8 ] || continue
  printf '%s\n' "$v" > "$vals"
  files=$( { git ls-files -z | xargs -0 grep -I -l -F -f "$vals" 2>/dev/null; for p in "$@"; do grep -r -l -F -f "$vals" "$p" 2>/dev/null; done; } | sort -u)
  for f in $files; do echo "  VALUE OF \$$n in: $f"; found=1; done
  if git log -p --all -S "$v" --format='%h' 2>/dev/null | grep -q .; then echo "  VALUE OF \$$n in git history"; found=1; fi
  echo "  checked \$$n (${#v} chars)"
done

echo "== 4. PNG text chunks"
for png in $(git ls-files '*.png') $(for p in "$@"; do find "$p" -name '*.png' 2>/dev/null; done); do
  if python3 - "$png" <<'EOF'
import struct, sys
data = open(sys.argv[1], 'rb').read()
pos, bad = 8, []
while pos + 8 <= len(data):
    length, kind = struct.unpack('>I4s', data[pos:pos + 8])
    if kind in (b'tEXt', b'iTXt', b'zTXt'): bad.append(kind.decode())
    pos += 12 + length
sys.exit(1 if bad else 0)
EOF
  then :; else echo "  TEXT CHUNK: $png"; found=1; fi
done
echo "  $(git ls-files '*.png' | wc -l) tracked screenshots checked"

if [ $found -eq 0 ]; then echo "RESULT: no secrets found"; else echo "RESULT: FOUND — see above"; fi
exit $found
