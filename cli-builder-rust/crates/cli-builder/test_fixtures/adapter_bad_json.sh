#!/bin/sh
# Mock adapter: emit truncated/invalid JSON, exit 0
echo '{"schemaVersion":"1","metadata":{"name":"Test'
exit 0
