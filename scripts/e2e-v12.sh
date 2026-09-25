#!/bin/bash
# v1.2 extended-contract checks + legacy shape regression. DB must be fresh-ish
# (run after xbs-e2e.sh wipe or standalone; uses its own syncs).
BASE="${BASE:-http://localhost:8090}"
V12="1.2.0"; V11="1.1.13"
PASS=0; FAIL=0
chk() { if [ "$2" = "$3" ]; then PASS=$((PASS+1)); echo "ok   $1";
  else FAIL=$((FAIL+1)); echo "FAIL $1: expected [$2] got [$3]"; fi }
J() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

echo "--- info capabilities ---"
chk "legacy info no caps" "False" "$(curl -s -H "Accept-Version: $V11" "$BASE/info" | J "'capabilities' in d")"
chk "v12 info caps" "True" "$(curl -s -H "Accept-Version: $V12" "$BASE/info" | J "'capabilities' in d")"
chk "v12 info version" "1.2.0" "$(curl -s -H "Accept-Version: $V12" "$BASE/info" | J "d['version']")"

echo "--- create ---"
C2=$(curl -s -H "Accept-Version: $V12" -H "Content-Type: application/json" -d '{"version":"1.8.0"}' "$BASE/bookmarks")
ID=$(echo "$C2" | J "d['id']"); REV=$(echo "$C2" | J "d['meta']['revision']"); HASH=$(echo "$C2" | J "d['meta']['hash']")
chk "v12 rev 1" "1" "$REV"; chk "v12 hash len" "64" "$(echo -n "$HASH" | wc -c)"
C1=$(curl -s -H "Accept-Version: $V11" -H "Content-Type: application/json" -d '{"version":"1.8.0"}' "$BASE/bookmarks")
chk "legacy create no meta" "False" "$(echo "$C1" | J "'meta' in d")"

echo "--- legacy get stays frozen ---"
chk "legacy get no meta" "False" "$(curl -s -H "Accept-Version: $V11" "$BASE/bookmarks/$ID" | J "'meta' in d")"
chk "v12 get has meta" "1" "$(curl -s -H "Accept-Version: $V12" "$BASE/bookmarks/$ID" | J "d['meta']['revision']")"

echo "--- baseRevision CAS ---"
P=$(curl -s -H "Accept-Version: $V12" -H "Content-Type: application/json" -d '{"bookmarks":"r2","baseRevision":1}' -X PUT "$BASE/bookmarks/$ID")
chk "rev CAS win" "2" "$(echo "$P" | J "d['meta']['revision']")"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V12" -H "Content-Type: application/json" -d '{"bookmarks":"rX","baseRevision":1}' -X PUT "$BASE/bookmarks/$ID")
chk "rev CAS stale" "409" "$R"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V12" -H "Content-Type: application/json" -d '{"bookmarks":"rX","lastUpdated":"1970-01-01T00:00:00.000Z"}' -X PUT "$BASE/bookmarks/$ID")
chk "ts CAS stale still 409" "409" "$R"

echo "--- HEAD / conditional ---"
H=$(curl -s -D - -o /dev/null -H "Accept-Version: $V12" -I "$BASE/bookmarks/$ID")
chk "head etag" "x" "$(echo "$H" | grep -qi etag && echo x || echo none)"
chk "head rev header" "2" "$(echo "$H" | grep -i "x-sync-revision" | tr -d '\r' | awk '{print $2}')"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V11" -I "$BASE/bookmarks/$ID")
chk "head legacy 412" "412" "$R"
HASH2=$(curl -s -H "Accept-Version: $V12" "$BASE/bookmarks/$ID" | J "d['meta']['hash']")
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V12" -H "If-None-Match: \"$HASH2\"" "$BASE/bookmarks/$ID")
chk "304 on match" "304" "$R"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V12" -H 'If-None-Match: "deadbeef"' "$BASE/bookmarks/$ID")
chk "200 on mismatch" "200" "$R"

echo "--- DELETE ---"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V11" -X DELETE "$BASE/bookmarks/$ID")
chk "delete legacy 412" "412" "$R"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V12" -X DELETE "$BASE/bookmarks/$ID")
chk "delete ok" "200" "$R"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V12" "$BASE/bookmarks/$ID")
chk "gone after delete" "401" "$R"

echo "=== pass=$PASS fail=$FAIL ==="
[ "$FAIL" -eq 0 ]
