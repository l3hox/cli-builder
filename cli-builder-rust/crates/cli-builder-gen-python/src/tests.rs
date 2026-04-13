//! Tests for the Python CLI generator.

use std::path::PathBuf;

use cli_builder_core::generator_model::LanguageProfile;
use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::*;

use crate::python_keywords;
use crate::python_mapper::PythonProfile;
use crate::renderer;

fn testsdk_fixture_path() -> PathBuf {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    PathBuf::from(manifest_dir)
        .join("../../..")
        .join("tests/fixtures/testsdk-metadata.json")
}

fn generate_testsdk(output_dir: &std::path::Path) {
    let json = std::fs::read_to_string(testsdk_fixture_path()).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();
    let profile = PythonProfile;
    let (model, _) = model_mapper::build(
        &envelope.metadata,
        &MapperOptions {
            cli_name: Some("testsdk-cli".into()),
        },
        &profile,
    );
    renderer::generate(&model, output_dir).unwrap();
}

// ================================================================
// Unit tests: tera_escape
// ================================================================

#[test]
fn tera_escape_breaks_double_braces() {
    assert_eq!(renderer::tera_escape("{{ var }}"), "{ { var } }");
}

#[test]
fn tera_escape_breaks_block_tags() {
    assert_eq!(renderer::tera_escape("{% if x %}"), "{ % if x % }");
}

#[test]
fn tera_escape_preserves_normal_text() {
    assert_eq!(renderer::tera_escape("hello world"), "hello world");
}

#[test]
fn tera_escape_handles_mixed_content() {
    let input = "Use {{ env 'SECRET' }} and {% raw %} here";
    let escaped = renderer::tera_escape(input);
    assert!(!escaped.contains("{{"));
    assert!(!escaped.contains("{%"));
}

// ================================================================
// Unit tests: PythonProfile type mapping
// ================================================================

fn tr(kind: TypeKind, name: &str) -> TypeRef {
    TypeRef {
        kind,
        name: name.to_string(),
        is_nullable: false,
        is_abstract: false,
        is_extensible_enum: false,
        generic_arguments: None,
        enum_values: None,
        properties: None,
        element_type: None,
        module: None,
    }
}

#[test]
fn python_map_primitive_types() {
    let p = PythonProfile;
    assert_eq!(p.map_primitive_type("str"), "str");
    assert_eq!(p.map_primitive_type("string"), "str");
    assert_eq!(p.map_primitive_type("String"), "str");
    assert_eq!(p.map_primitive_type("int"), "int");
    assert_eq!(p.map_primitive_type("Int32"), "int");
    assert_eq!(p.map_primitive_type("long"), "int");
    assert_eq!(p.map_primitive_type("float"), "float");
    assert_eq!(p.map_primitive_type("double"), "float");
    assert_eq!(p.map_primitive_type("Double"), "float");
    assert_eq!(p.map_primitive_type("decimal"), "float");
    assert_eq!(p.map_primitive_type("bool"), "bool");
    assert_eq!(p.map_primitive_type("Boolean"), "bool");
    assert_eq!(p.map_primitive_type("void"), "None");
    assert_eq!(p.map_primitive_type("TimeSpan"), "str");
    assert_eq!(p.map_primitive_type("UnknownType"), "str"); // catch-all
}

#[test]
fn python_map_cli_type_for_cli_param() {
    let p = PythonProfile;
    assert_eq!(p.map_cli_type(&tr(TypeKind::Primitive, "int"), true), "int");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Enum, "Status"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Class, "Options"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Array, "int[]"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Dictionary, "Dict"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Generic, "List"), true), "str");
}

#[test]
fn python_map_cli_type_for_return() {
    let p = PythonProfile;
    assert_eq!(p.map_cli_type(&tr(TypeKind::Class, "Customer"), false), "Customer");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Generic, "List"), false), "List");
}

#[test]
fn python_is_binary_type() {
    let p = PythonProfile;
    assert!(p.is_binary_type("bytes"));
    assert!(p.is_binary_type("BinaryContent")); // .NET compat
    assert!(p.is_binary_type("Stream"));
    assert!(!p.is_binary_type("Customer"));
}

#[test]
fn python_is_unwirable_return_type() {
    let p = PythonProfile;
    assert!(p.is_unwirable_return_type("AsyncIterator"));
    assert!(p.is_unwirable_return_type("ChatClient"));
    assert!(p.is_unwirable_return_type("T")); // single-char generic
    assert!(!p.is_unwirable_return_type("Customer"));
}

// ================================================================
// Unit tests: Python keywords
// ================================================================

#[test]
fn python_keywords_detected() {
    assert!(python_keywords::is_keyword("class"));
    assert!(python_keywords::is_keyword("def"));
    assert!(python_keywords::is_keyword("return"));
    assert!(python_keywords::is_keyword("yield"));
    // Builtins
    assert!(python_keywords::is_keyword("id"));
    assert!(python_keywords::is_keyword("list"));
    assert!(python_keywords::is_keyword("type"));
    assert!(python_keywords::is_keyword("input"));
    // Not keywords
    assert!(!python_keywords::is_keyword("customer"));
    assert!(!python_keywords::is_keyword("foobar"));
}

#[test]
fn python_boilerplate_names_detected() {
    assert!(python_keywords::is_boilerplate_name("json"));
    assert!(python_keywords::is_boilerplate_name("click"));
    assert!(python_keywords::is_boilerplate_name("ctx"));
    assert!(!python_keywords::is_boilerplate_name("customer"));
}

// ================================================================
// Structural assertions (Phase 3 — no golden files)
// ================================================================

#[test]
fn generates_expected_file_count() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let py_files: Vec<_> = walkdir::WalkDir::new(dir.path())
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.path().extension().map_or(false, |ext| ext == "py"))
        .collect();

    // Exact expected count:
    // __init__.py + __main__.py + cli.py = 3
    // commands/__init__.py + 7 resource files = 8
    // output/__init__.py + json_formatter.py + table_formatter.py = 3
    // auth/__init__.py + handler.py = 2
    // Total = 16
    assert_eq!(
        py_files.len(),
        16,
        "Expected exactly 16 .py files, got {}",
        py_files.len()
    );
}

#[test]
fn generates_pyproject_toml() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let toml = std::fs::read_to_string(dir.path().join("pyproject.toml")).unwrap();
    assert!(toml.contains("testsdk-cli"));
    assert!(toml.contains("click>=8.0"));
    assert!(toml.contains("[project.scripts]"));
}

#[test]
fn cli_py_has_click_patterns() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let cli_py = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/cli.py"),
    )
    .unwrap();
    assert!(cli_py.contains("import click"));
    assert!(cli_py.contains("@click.group()"));
    assert!(cli_py.contains("def cli("));
    assert!(cli_py.contains("def main():"));
    assert!(cli_py.contains("cli.add_command(customer)"));
    assert!(cli_py.contains("--api-key"));
}

#[test]
fn resource_files_have_click_commands() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let customer_py = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/commands/customer.py"),
    )
    .unwrap();
    assert!(customer_py.contains("import click"));
    assert!(customer_py.contains("@click.group()"));
    assert!(customer_py.contains("def customer("));
    assert!(customer_py.contains(".command(name="));
    assert!(customer_py.contains("def get("));
    assert!(customer_py.contains("def create("));
    assert!(customer_py.contains("_get_client"));
}

#[test]
fn auth_handler_references_env_var() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let handler = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/auth/handler.py"),
    )
    .unwrap();
    assert!(handler.contains("def resolve_credential("));
    assert!(handler.contains("os.environ.get("));
    assert!(handler.contains("TESTSDK_APIKEY"));
}

#[test]
fn all_generated_python_files_pass_ast_parse() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    // Collect all .py files
    let py_files: Vec<_> = walkdir::WalkDir::new(dir.path())
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.path().extension().map_or(false, |ext| ext == "py"))
        .map(|e| e.path().to_path_buf())
        .collect();

    assert!(!py_files.is_empty(), "No .py files found");

    // Run ast.parse on each file
    for path in &py_files {
        let output = std::process::Command::new("python3")
            .args([
                "-c",
                &format!(
                    "import ast; ast.parse(open('{}').read())",
                    path.display()
                ),
            ])
            .output()
            .expect("Failed to run python3");

        assert!(
            output.status.success(),
            "ast.parse failed for {}: {}",
            path.display(),
            String::from_utf8_lossy(&output.stderr)
        );
    }
}

#[test]
fn keyword_param_id_gets_renamed_flag() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let customer_py = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/commands/customer.py"),
    )
    .unwrap();

    // "id" is a Python builtin — should be renamed to --id-value
    assert!(customer_py.contains("--id-value"));
}

// ================================================================
// Council fix: adversarial env_var escaping
// ================================================================

#[test]
fn adversarial_env_var_is_escaped_in_output() {
    use cli_builder_core::generator_model::*;

    // Build a minimal model with a malicious env_var
    let model = GeneratorModel {
        cli_name: "test-cli".into(),
        sdk_name: "TestSdk".into(),
        sdk_version: "1.0.0".into(),
        sdk_package_name: "testsdk".into(),
        root_namespace: "TestCli".into(),
        cli_description: "test".into(),
        resources: vec![],
        auth: Some(AuthModel {
            auth_type: "ApiKey".into(),
            env_var: "FOO{{ malicious }}BAR".into(),
            parameter_name: "apiKey".into(),
        }),
        static_auth_setup: None,
    };

    let dir = tempfile::tempdir().unwrap();
    renderer::generate(&model, dir.path()).unwrap();

    let handler = std::fs::read_to_string(
        dir.path().join("src/test_cli/auth/handler.py"),
    )
    .unwrap();

    // The {{ }} must be broken by tera_escape — should NOT appear verbatim
    assert!(!handler.contains("{{"), "Unescaped {{ in auth handler");
    assert!(!handler.contains("}}"), "Unescaped }} in auth handler");
    assert!(handler.contains("FOO"), "Env var content missing");

    let cli_py = std::fs::read_to_string(
        dir.path().join("src/test_cli/cli.py"),
    )
    .unwrap();
    assert!(!cli_py.contains("{{"), "Unescaped {{ in cli.py");
}

// ================================================================
// Council fix: echo-stub path for unwirable operations
// ================================================================

#[test]
fn unwirable_operation_generates_echo_stub() {
    use cli_builder_core::generator_model::*;

    // Synthetic model with one unwirable operation (can_wire_sdk_call = false)
    let model = GeneratorModel {
        cli_name: "stub-cli".into(),
        sdk_name: "StubSdk".into(),
        sdk_version: "1.0.0".into(),
        sdk_package_name: "stubsdk".into(),
        root_namespace: "StubCli".into(),
        cli_description: "test".into(),
        resources: vec![ResourceModel {
            name: "thing".into(),
            class_name: "Thing".into(),
            description: None,
            operations: vec![OperationModel {
                name: "upload".into(),
                method_name: "Upload".into(),
                description: Some("Upload binary data".into()),
                parameters: vec![],
                needs_json_input: false,
                return_type_name: "None".into(),
                is_streaming: false,
                source_method_name: None,
                options_type_name: None,
                method_params: vec![],
                can_wire_sdk_call: false, // echo stub
                has_json_direct_params: false,
                requires_sentinel_nullability: false,
            }],
            source_class_name: Some("ThingService".into()),
            source_module: Some("sdk.services".into()),
            can_construct: true,
            constructor_auth: None,
            constructor_config_params: vec![],
            required_modules: vec![],
        }],
        auth: None,
        static_auth_setup: None,
    };

    let dir = tempfile::tempdir().unwrap();
    renderer::generate(&model, dir.path()).unwrap();

    let thing_py = std::fs::read_to_string(
        dir.path().join("src/stub_cli/commands/thing.py"),
    )
    .unwrap();
    assert!(thing_py.contains("not yet wired to SDK"));
    assert!(thing_py.contains("@click.pass_context"));
    assert!(thing_py.contains(".command(name=\"upload\")"));

    // Must still be valid Python
    let output = std::process::Command::new("python3")
        .args(["-c", &format!("import ast; ast.parse(open('{}').read())",
            dir.path().join("src/stub_cli/commands/thing.py").display())])
        .output()
        .expect("Failed to run python3");
    assert!(output.status.success(), "Echo stub file failed ast.parse");
}

// ================================================================
// Council fix: output/__init__.py is empty (not json_formatter copy)
// ================================================================

#[test]
fn output_init_py_is_empty() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let init = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/output/__init__.py"),
    )
    .unwrap();
    assert!(init.is_empty(), "output/__init__.py should be empty, got: {}", init);
}
