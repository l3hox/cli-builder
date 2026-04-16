"""Tests for JSON serialization — camelCase, null handling, schemaVersion, enum values."""

import json

from cli_builder_adapter.json_output import serialize_adapter_result, serialize_metadata
from cli_builder_adapter.models import (
    AdapterResult,
    AuthPattern,
    AuthType,
    Diagnostic,
    DiagnosticSeverity,
    Operation,
    Parameter,
    Resource,
    SdkMetadata,
    StaticAuthConfig,
    AuthSetupStyle,
    TypeKind,
    TypeRef,
)


def _minimal_metadata() -> SdkMetadata:
    return SdkMetadata(
        name="TestSdk",
        version="1.0.0",
        resources=[
            Resource(
                name="customer",
                description=None,
                operations=[
                    Operation(
                        name="get",
                        description=None,
                        parameters=[
                            Parameter(
                                name="id",
                                type=TypeRef(kind=TypeKind.PRIMITIVE, name="str"),
                                required=True,
                            )
                        ],
                        return_type=TypeRef(kind=TypeKind.CLASS, name="Customer"),
                    )
                ],
                source_class_name="CustomerClient",
                source_module="test_sdk.services",
            )
        ],
        auth_patterns=[
            AuthPattern(
                type=AuthType.API_KEY,
                env_var="TESTSDK_API_KEY",
                parameter_name="api_key",
            )
        ],
    )


class TestCamelCaseKeys:
    def test_top_level_keys_are_camel_case(self):
        result = AdapterResult(metadata=_minimal_metadata())
        output = json.loads(serialize_adapter_result(result))
        assert "schemaVersion" in output
        assert "metadata" in output
        assert "diagnostics" in output

    def test_metadata_keys_are_camel_case(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        assert "authPatterns" in output
        assert "staticAuth" in output  # null but present

    def test_resource_keys_are_camel_case(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        resource = output["resources"][0]
        assert "sourceClassName" in resource
        assert "sourceModule" in resource
        assert "hasParameterlessCtor" in resource

    def test_type_ref_keys_are_camel_case(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        param_type = output["resources"][0]["operations"][0]["parameters"][0]["type"]
        assert "isNullable" in param_type
        assert "isAbstract" in param_type
        assert "isExtensibleEnum" in param_type
        assert "genericArguments" in param_type
        assert "enumValues" in param_type
        assert "elementType" in param_type


class TestNullHandling:
    def test_null_fields_present_not_absent(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        resource = output["resources"][0]
        assert resource["description"] is None
        assert resource["constructorParams"] is None

    def test_static_auth_null_when_absent(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        assert "staticAuth" in output
        assert output["staticAuth"] is None


class TestSchemaVersion:
    def test_schema_version_present(self):
        result = AdapterResult(metadata=_minimal_metadata())
        output = json.loads(serialize_adapter_result(result))
        assert output["schemaVersion"] == "1"

    def test_schema_version_is_first_key(self):
        result = AdapterResult(metadata=_minimal_metadata())
        output = json.loads(serialize_adapter_result(result))
        assert list(output.keys())[0] == "schemaVersion"


class TestEnumSerialization:
    def test_type_kind_as_camel_case_string(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        param_type = output["resources"][0]["operations"][0]["parameters"][0]["type"]
        assert param_type["kind"] == "primitive"

    def test_auth_type_as_camel_case_string(self):
        metadata = _minimal_metadata()
        output = json.loads(serialize_metadata(metadata))
        assert output["authPatterns"][0]["type"] == "apiKey"

    def test_diagnostic_severity_as_camel_case(self):
        result = AdapterResult(
            metadata=_minimal_metadata(),
            diagnostics=[Diagnostic(DiagnosticSeverity.WARNING, "CB001", "test warning")],
        )
        output = json.loads(serialize_adapter_result(result))
        assert output["diagnostics"][0]["severity"] == "warning"


class TestStaticAuthConfig:
    def test_static_auth_serialization(self):
        metadata = _minimal_metadata()
        metadata.static_auth = StaticAuthConfig(
            type_name="StripeConfiguration",
            type_module="stripe",
            property_name="api_key",
            style=AuthSetupStyle.MODULE_ATTRIBUTE,
        )
        output = json.loads(serialize_metadata(metadata))
        sa = output["staticAuth"]
        assert sa["typeName"] == "StripeConfiguration"
        assert sa["typeModule"] == "stripe"
        assert sa["propertyName"] == "api_key"
        assert sa["style"] == "moduleAttribute"
