//! Tests for SdkMetadata deserialization against real adapter fixtures.

use crate::models::*;

/// Path to the .NET TestSdk fixture (relative to workspace root).
fn testsdk_fixture_path() -> std::path::PathBuf {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    std::path::PathBuf::from(manifest_dir)
        .join("../../..")  // cli-builder-rust/crates/cli-builder-core → repo root
        .join("tests/fixtures/testsdk-metadata.json")
}

#[test]
fn deserialize_testsdk_fixture() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {}: {}", path.display(), e));
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json)
        .unwrap_or_else(|e| panic!("Failed to deserialize: {}", e));

    // schema_version may be absent in pre-Step-12 fixtures
    if let Some(ref v) = envelope.schema_version {
        assert_eq!(v, "1");
    }
    assert!(!envelope.metadata.resources.is_empty(), "Expected resources, got empty");
    assert!(envelope.metadata.resources.len() >= 7, "Expected >= 7 resources, got {}", envelope.metadata.resources.len());
}

#[test]
fn testsdk_has_expected_resources() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    let names: Vec<&str> = envelope.metadata.resources.iter().map(|r| r.name.as_str()).collect();
    assert!(names.contains(&"customer"), "Missing 'customer' resource");
    assert!(names.contains(&"order"), "Missing 'order' resource");
    assert!(names.contains(&"message"), "Missing 'message' resource");
}

#[test]
fn testsdk_customer_has_operations() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    let customer = envelope.metadata.resources.iter()
        .find(|r| r.name == "customer")
        .expect("customer resource not found");
    let op_names: Vec<&str> = customer.operations.iter().map(|o| o.name.as_str()).collect();
    assert!(op_names.contains(&"get"), "Missing 'get' operation");
    assert!(op_names.contains(&"create"), "Missing 'create' operation");
}

#[test]
fn testsdk_type_kinds_deserialize() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    // Find a primitive type (string parameter)
    let customer = envelope.metadata.resources.iter()
        .find(|r| r.name == "customer").unwrap();
    let get_op = customer.operations.iter()
        .find(|o| o.name == "get").unwrap();
    let id_param = &get_op.parameters[0];
    assert_eq!(id_param.type_ref.kind, TypeKind::Primitive);
}

#[test]
fn deserialize_python_adapter_output() {
    // Parse the Python adapter's JSON output if available
    let path = std::path::PathBuf::from("/tmp/python-adapter-output.json");
    if !path.exists() {
        eprintln!("Skipping: /tmp/python-adapter-output.json not found (run Python adapter first)");
        return;
    }
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json)
        .unwrap_or_else(|e| panic!("Failed to deserialize Python adapter output: {}", e));

    assert_eq!(envelope.schema_version.as_deref(), Some("1"));
    assert!(!envelope.metadata.resources.is_empty());
    assert!(envelope.metadata.resources.len() >= 3, "Expected >= 3 resources from Python TestSdk");
}

#[test]
fn testsdk_auth_patterns_deserialize() {
    let path = testsdk_fixture_path();
    let json = std::fs::read_to_string(&path).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();

    assert!(!envelope.metadata.auth_patterns.is_empty(), "Expected auth patterns");
    assert_eq!(envelope.metadata.auth_patterns[0].auth_type, AuthType::ApiKey);
}
