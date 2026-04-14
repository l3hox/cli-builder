#!/bin/sh
# Mock adapter: emit valid AdapterResultEnvelope JSON, exit 0
cat <<'EOF'
{"schemaVersion":"1","metadata":{"name":"TestSdk","version":"1.0.0","resources":[{"name":"customer","description":"Customer resource","operations":[{"name":"get","description":"Get a customer","parameters":[{"name":"id","type":{"kind":"primitive","name":"str","isNullable":false,"isAbstract":false,"isExtensibleEnum":false},"required":true}],"returnType":{"kind":"class","name":"Customer","isNullable":false,"isAbstract":false,"isExtensibleEnum":false},"isStreaming":false}],"sourceClassName":"CustomerClient","sourceModule":"test_sdk.services","hasParameterlessCtor":false}],"authPatterns":[{"type":"apiKey","envVar":"TEST_API_KEY","parameterName":"api_key"}],"staticAuth":null},"diagnostics":[{"severity":"info","code":"CB601","message":"Package imported at runtime"}]}
EOF
exit 0
