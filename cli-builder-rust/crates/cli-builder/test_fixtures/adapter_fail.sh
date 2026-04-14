#!/bin/sh
# Mock adapter: environment failure, exit 2
cat <<'EOF'
{"schemaVersion":"1","metadata":{"name":"","version":"0.0.0","resources":[],"authPatterns":[],"staticAuth":null},"diagnostics":[{"severity":"error","code":"CB600","message":"Could not import package"}]}
EOF
exit 2
