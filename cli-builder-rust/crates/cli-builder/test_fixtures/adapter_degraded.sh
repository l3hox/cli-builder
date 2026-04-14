#!/bin/sh
# Mock adapter: emit partial metadata with Error diagnostic, exit 1
cat <<'EOF'
{"schemaVersion":"1","metadata":{"name":"TestSdk","version":"1.0.0","resources":[],"authPatterns":[],"staticAuth":null},"diagnostics":[{"severity":"error","code":"CB100","message":"Some types could not be extracted"}]}
EOF
exit 1
