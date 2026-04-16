//! Verifies mock JSON payloads deserialize into the same AdapterResultEnvelope
//! struct the orchestrator uses. Schema drift breaks this test immediately.

use cli_builder_core::models::AdapterResultEnvelope;

const OK_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"TestSdk","version":"1.0.0","resources":[{"name":"customer","description":"Customer resource","operations":[{"name":"get","description":"Get a customer","parameters":[{"name":"id","type":{"kind":"primitive","name":"str","isNullable":false,"isAbstract":false,"isExtensibleEnum":false},"required":true}],"returnType":{"kind":"class","name":"Customer","isNullable":false,"isAbstract":false,"isExtensibleEnum":false},"isStreaming":false}],"sourceClassName":"CustomerClient","sourceModule":"test_sdk.services","hasParameterlessCtor":false}],"authPatterns":[{"type":"apiKey","envVar":"TEST_API_KEY","parameterName":"api_key"}],"staticAuth":null},"diagnostics":[{"severity":"info","code":"CB601","message":"Package imported at runtime"}]}"#;

const DEGRADED_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"TestSdk","version":"1.0.0","resources":[],"authPatterns":[],"staticAuth":null},"diagnostics":[{"severity":"error","code":"CB100","message":"Some types could not be extracted"}]}"#;

const FAIL_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"","version":"0.0.0","resources":[],"authPatterns":[],"staticAuth":null},"diagnostics":[{"severity":"error","code":"CB600","message":"Could not import package"}]}"#;

#[test]
fn ok_json_deserializes_to_envelope() {
    let envelope: AdapterResultEnvelope = serde_json::from_str(OK_JSON).unwrap();
    assert_eq!(envelope.metadata.name, "TestSdk");
    assert_eq!(envelope.metadata.resources.len(), 1);
    assert_eq!(envelope.diagnostics.len(), 1);
}

#[test]
fn degraded_json_deserializes_to_envelope() {
    let envelope: AdapterResultEnvelope = serde_json::from_str(DEGRADED_JSON).unwrap();
    assert_eq!(envelope.metadata.name, "TestSdk");
    assert!(envelope.metadata.resources.is_empty());
}

#[test]
fn fail_json_deserializes_to_envelope() {
    let envelope: AdapterResultEnvelope = serde_json::from_str(FAIL_JSON).unwrap();
    assert!(envelope.metadata.name.is_empty());
    assert_eq!(envelope.diagnostics[0].code, "CB600");
}
