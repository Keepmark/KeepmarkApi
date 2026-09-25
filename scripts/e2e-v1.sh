#!/bin/bash
# E2E parity check vs Node api v1.1.13. Exits non-zero on first mismatch.
BASE="${BASE:-http://localhost:8080}"
V="1.1.13"
PASS=0; FAIL=0
chk() { # chk <desc> <expected> <actual>
  if [ "$2" = "$3" ]; then PASS=$((PASS+1)); echo "ok   $1";
  else FAIL=$((FAIL+1)); echo "FAIL $1: expected [$2] got [$3]"; fi
}
code_of() { python3 -c "import json,sys; print(json.load(sys.stdin).get('code',''))" 2>/dev/null; }

echo "--- info ---"
INFO=$(curl -s -H "Accept-Version: $V" "$BASE/info")
chk "info.status" "1" "$(echo "$INFO" | python3 -c "import json,sys; print(json.load(sys.stdin)['status'])")"
chk "info.version" "1.2.0" "$(echo "$INFO" | python3 -c "import json,sys; print(json.load(sys.stdin)['version'])")"

echo "--- create v2 ---"
C=$(curl -s -H "Accept-Version: $V" -H "Content-Type: application/json" -d '{"version":"1.8.0"}' "$BASE/bookmarks")
ID=$(echo "$C" | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])")
LU1=$(echo "$C" | python3 -c "import json,sys; print(json.load(sys.stdin)['lastUpdated'])")
chk "create id len" "32" "$(echo -n "$ID" | wc -c)"
chk "create version" "1.8.0" "$(echo "$C" | python3 -c "import json,sys; print(json.load(sys.stdin)['version'])")"

echo "--- get ---"
G=$(curl -s -H "Accept-Version: $V" "$BASE/bookmarks/$ID")
chk "get version" "1.8.0" "$(echo "$G" | python3 -c "import json,sys; print(json.load(sys.stdin)['version'])")"

echo "--- put v2 no base ---"
P=$(curl -s -H "Accept-Version: $V" -H "Content-Type: application/json" -d '{"bookmarks":"blob1"}' -X PUT "$BASE/bookmarks/$ID")
LU2=$(echo "$P" | python3 -c "import json,sys; print(json.load(sys.stdin)['lastUpdated'])")
[ -n "$LU2" ] && [ "$LU2" != "None" ] && chk "put returns date" "x" "x" || chk "put returns date" "x" "empty"

echo "--- lastUpdated ---"
L=$(curl -s -H "Accept-Version: $V" "$BASE/bookmarks/$ID/lastUpdated")
chk "lastUpdated matches" "$LU2" "$(echo "$L" | python3 -c "import json,sys; print(json.load(sys.stdin)['lastUpdated'])")"

echo "--- conflict ---"
R=$(curl -s -o /tmp/opencode/xbs-body -w "%{http_code}" -H "Accept-Version: $V" -H "Content-Type: application/json" \
  -d '{"bookmarks":"blobX","lastUpdated":"1970-01-01T00:00:00.000Z"}' -X PUT "$BASE/bookmarks/$ID")
chk "stale put status" "409" "$R"
chk "stale put code" "SyncConflictException" "$(code_of < /tmp/opencode/xbs-body)"

echo "--- put with correct base ---"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: $V" -H "Content-Type: application/json" \
  -d "{\"bookmarks\":\"blob2\",\"lastUpdated\":\"$LU2\",\"version\":\"1.8.0\"}" -X PUT "$BASE/bookmarks/$ID")
chk "fresh put status" "200" "$R"

echo "--- version ---"
chk "get version" "1.8.0" "$(curl -s -H "Accept-Version: $V" "$BASE/bookmarks/$ID/version" | python3 -c "import json,sys; print(json.load(sys.stdin)['version'])")"

echo "--- errors ---"
R=$(curl -s -o /tmp/opencode/xbs-body -w "%{http_code}" "$BASE/bookmarks/invalidid"); chk "bad id status" "401" "$R"
chk "bad id code" "InvalidSyncIdException" "$(code_of < /tmp/opencode/xbs-body)"
R=$(curl -s -o /tmp/opencode/xbs-body -w "%{http_code}" "$BASE/bookmarks/426e20b680054872a46c3e1ce9a2c54e"); chk "missing status" "401" "$R"
chk "missing code" "SyncNotFoundException" "$(code_of < /tmp/opencode/xbs-body)"
R=$(curl -s -o /tmp/opencode/xbs-body -w "%{http_code}" -H "Accept-Version: $V" -H "Content-Type: application/json" -d '{}' "$BASE/bookmarks"); chk "empty post" "400" "$R"
chk "empty post code" "RequiredDataNotFoundException" "$(code_of < /tmp/opencode/xbs-body)"
R=$(curl -s -o /dev/null -w "%{http_code}" -H "Accept-Version: 0.0.0" -H "Content-Type: application/json" -d '{"bookmarks":"x"}' -X PUT "$BASE/bookmarks/$ID"); chk "unsupported version" "412" "$R"
R=$(curl -s -o /tmp/opencode/xbs-body -w "%{http_code}" "$BASE/bookmarks"); chk "unknown route" "404" "$R"
chk "unknown route code" "NotImplementedException" "$(code_of < /tmp/opencode/xbs-body)"

echo "--- v1 compat (Accept-Version 1.0.5) ---"
C1=$(curl -s -H "Accept-Version: 1.0.5" -H "Content-Type: application/json" -d '{"bookmarks":"v1blob"}' "$BASE/bookmarks")
chk "v1 create has id" "32" "$(echo "$C1" | python3 -c "import json,sys; print(len(json.load(sys.stdin)['id']))")"
chk "v1 create no version" "False" "$(echo "$C1" | python3 -c "import json,sys; print('version' in json.load(sys.stdin))")"
R=$(curl -s -o /tmp/opencode/xbs-body -w "%{http_code}" -H "Accept-Version: 1.0.5" -H "Content-Type: application/json" \
  -d '{"bookmarks":"z"}' -X PUT "$BASE/bookmarks/426e20b680054872a46c3e1ce9a2c54e"); chk "v1 put missing" "200" "$R"
chk "v1 put missing empty" "{}" "$(cat /tmp/opencode/xbs-body | python3 -c "import json,sys; print(json.dumps(json.load(sys.stdin),separators=(',',':')))" )"

echo "=== pass=$PASS fail=$FAIL ==="
[ "$FAIL" -eq 0 ]
