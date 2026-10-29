#!/usr/bin/env bash
# Harness for .github/workflows/release.yml: runs the importer-pick, version, notes, contract and
# zip logic verbatim against the real repo, with `gh` stubbed so nothing is published.
#
# .NET is not installed in the container, so the publish step is simulated by fabricating the
# output directory the real one produces — which is enough to exercise the checks that guard it.
set -uo pipefail

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok   - $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL - $1"; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected '$3', got '$2')"; fi; }

cd /work

echo "== workflow yaml parses and has the expected shape =="
python3 - <<'PY'
import yaml
d = yaml.safe_load(open('.github/workflows/release.yml'))
# YAML 1.1: a bare `on:` key loads as boolean True, not the string "on".
triggers = d.get('on', d.get(True))
assert 'workflow_dispatch' in triggers, triggers
inputs = (triggers['workflow_dispatch'] or {}).get('inputs', {})
assert 'importer' in inputs, inputs
assert inputs['importer']['type'] == 'choice', inputs['importer']
steps = [s['name'] for s in d['jobs']['build-and-release']['steps']]
expected = ['Checkout','Pick the importer','Read version from CHANGELOG',
            'Verify PluginMetadata matches the CHANGELOG','Verify the vendored contract is present',
            'Refuse to overwrite an existing release','Extract release notes from CHANGELOG',
            'Setup .NET','Publish','Verify the contract is not shipped','Collect the zip',
            'Create release']
assert steps == expected, steps
body = open('.github/workflows/release.yml').read()
assert body.count('gh release create') == 1, body.count('gh release create')
print("  ok   - yaml structure, one importer input, single release")
PY
[ $? -eq 0 ] && PASS=$((PASS+1)) || FAIL=$((FAIL+1))

echo
echo "== the importer is picked out of a commit message =="
pick() { printf '%s' "$1" | grep -oP '\[release[ \t]+\K[A-Za-z0-9_]+' | head -1 || true; }
check "a named release is picked up"        "$(pick 'fix: thing [release Ranks_Importer]')" "Ranks_Importer"
check "tabs work too"                       "$(pick '[release	Ranks_Importer]')"            "Ranks_Importer"
check "an ordinary commit picks nothing"    "$(pick 'chore: tidy up')"                      ""
# A bare [release] must not silently release something arbitrary.
check "a bare [release] picks nothing"      "$(pick 'chore: [release]')"                    ""

echo
echo "== every importer in the repo satisfies the release contract =="
for DIR in */; do
  IMPORTER="${DIR%/}"
  [ -f "$IMPORTER/$IMPORTER.csproj" ] || continue

  VERSION=$(grep -m1 -oP '^##[ \t]*\[v?\K[^]]+' "$IMPORTER/CHANGELOG.md" 2>/dev/null || true)
  if [ -n "$VERSION" ]; then ok "$IMPORTER CHANGELOG declares $VERSION"
  else bad "$IMPORTER has no '## [x.y.z]' CHANGELOG section"; continue; fi

  DECLARED=$(sed -n 's/.*Version[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' "$IMPORTER/src/$IMPORTER.cs" | head -1)
  check "$IMPORTER PluginMetadata agrees" "$DECLARED" "$VERSION"

  if ls "$IMPORTER"/deps/*.Contract.dll >/dev/null 2>&1; then
    ok "$IMPORTER vendors its provider contract, so it builds without that repo"
  else
    bad "$IMPORTER/deps holds no *.Contract.dll"
  fi

  awk -v ver="$VERSION" '
    /^## \[/ { if (found) exit; if ($0 ~ "\\[v?" ver "\\]") { found = 1; next } }
    found { print }
  ' "$IMPORTER/CHANGELOG.md" > /tmp/notes.md
  if [ -s /tmp/notes.md ]; then ok "$IMPORTER release notes are not empty"
  else bad "$IMPORTER has an empty '## [$VERSION]' section"; fi
done

echo
echo "== a shipped contract assembly is caught =="
# Shipping it gives the provider's interface a second type identity and the handshake stops
# matching, with nothing in the server log to explain it.
scan() { find "$1" -name '*.Contract.dll' -type f 2>/dev/null || true; }

rm -rf /tmp/pub && mkdir -p /tmp/pub/Ranks_Importer
touch /tmp/pub/Ranks_Importer/Ranks_Importer.dll
check "a clean publish passes" "$(scan /tmp/pub)" ""

touch /tmp/pub/Ranks_Importer/Ranks.Contract.dll
if [ -n "$(scan /tmp/pub)" ]; then ok "a stowaway contract is detected"
else bad "a stowaway contract went unnoticed"; fi

echo
echo "== the zip is located and inspected =="
rm -rf /tmp/zt && mkdir -p /tmp/zt/Ranks_Importer/build/publish/Ranks_Importer
cd /tmp/zt
touch Ranks_Importer/build/publish/Ranks_Importer/Ranks_Importer.dll
(cd Ranks_Importer/build/publish && zip -qr ../Ranks_Importer-v1.0.0.zip Ranks_Importer)

ZIP="Ranks_Importer/build/Ranks_Importer-v1.0.0.zip"
[ -f "$ZIP" ] && ok "the expected zip path resolves" || bad "zip not found at $ZIP"
if unzip -l "$ZIP" | grep -q '\.Contract\.dll'; then bad "clean zip reported a contract"; else ok "a clean zip passes the contract grep"; fi

touch Ranks_Importer/build/publish/Ranks_Importer/Ranks.Contract.dll
rm -f "$ZIP"
(cd Ranks_Importer/build/publish && zip -qr ../Ranks_Importer-v1.0.0.zip Ranks_Importer)
if unzip -l "$ZIP" | grep -q '\.Contract\.dll'; then ok "a poisoned zip is caught"; else bad "a poisoned zip slipped through"; fi

MISSING=$(find "Ranks_Importer/build" -maxdepth 1 -name "Ranks_Importer-v9.9.9.zip" -type f | head -1)
check "a wrong-version zip is not matched" "$MISSING" ""
cd /work

echo
echo "== gh is never asked to publish from here =="
mkdir -p /tmp/bin
cat > /tmp/bin/gh <<'EOF'
#!/usr/bin/env bash
if [ "${1:-}" = "release" ] && [ "${2:-}" = "view" ]; then exit 1; fi
echo "STUB gh $*" >> /tmp/gh.log
EOF
chmod +x /tmp/bin/gh
export PATH="/tmp/bin:$PATH"
: > /tmp/gh.log
gh release view "Ranks_Importer-v1.0.0" >/dev/null 2>&1 && bad "the stub claims the tag exists" || ok "an unreleased tag is reported absent"
check "nothing was published" "$(wc -l < /tmp/gh.log | tr -d ' ')" "0"

echo
echo "================================"
echo "passed: $PASS   failed: $FAIL"
[ "$FAIL" -eq 0 ]
