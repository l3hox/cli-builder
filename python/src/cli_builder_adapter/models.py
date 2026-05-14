"""SdkMetadata model types — mirrors the C# Core models for JSON compatibility."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum


class TypeKind(Enum):
    PRIMITIVE = "primitive"
    ENUM = "enum"
    CLASS = "class"
    GENERIC = "generic"
    ARRAY = "array"
    DICTIONARY = "dictionary"
    OTHER = "other"


class DiagnosticSeverity(Enum):
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"


class AuthType(Enum):
    API_KEY = "apiKey"
    BEARER_TOKEN = "bearerToken"
    OAUTH = "oAuth"
    CUSTOM = "custom"


class AuthSetupStyle(Enum):
    STATIC_PROPERTY = "staticProperty"
    MODULE_ATTRIBUTE = "moduleAttribute"


@dataclass
class TypeRef:
    kind: TypeKind
    name: str
    is_nullable: bool = False
    is_abstract: bool = False
    is_extensible_enum: bool = False
    generic_arguments: list[TypeRef] | None = None
    enum_values: list[str] | None = None
    properties: list[Parameter] | None = None
    element_type: TypeRef | None = None
    module: str | None = None


@dataclass
class Parameter:
    name: str
    type: TypeRef
    required: bool
    default_value: object | None = None
    description: str | None = None


@dataclass
class Operation:
    name: str
    description: str | None
    parameters: list[Parameter]
    return_type: TypeRef
    is_streaming: bool = False
    source_method_name: str | None = None


@dataclass
class ConstructorParam:
    name: str
    type_name: str
    type_module: str | None
    is_auth: bool
    is_required: bool


@dataclass
class StaticAuthConfig:
    type_name: str
    type_module: str
    property_name: str
    style: AuthSetupStyle = AuthSetupStyle.MODULE_ATTRIBUTE


@dataclass
class AuthPattern:
    type: AuthType
    env_var: str
    parameter_name: str
    header_name: str | None = None
    description: str | None = None


@dataclass
class Diagnostic:
    severity: DiagnosticSeverity
    code: str
    message: str


@dataclass
class Resource:
    name: str
    description: str | None
    operations: list[Operation]
    source_class_name: str | None = None
    source_module: str | None = None
    constructor_params: list[ConstructorParam] | None = None
    has_parameterless_ctor: bool = False


@dataclass
class SdkMetadata:
    name: str
    version: str
    resources: list[Resource]
    auth_patterns: list[AuthPattern]
    static_auth: StaticAuthConfig | None = None
    # Discovery-mode provenance — which adapter discovery path produced this
    # metadata. See ADR-023. Default keeps existing Stripe-derived JSON
    # consumers round-tripping unchanged.
    discovery_mode: str = "multi_service"
    # PyPI distribution name when it differs from `name` (the Python import
    # name). PyGithub installs as "PyGithub" but imports as "github" — this
    # field lets the generator emit the correct pip dependency. None when
    # the package wasn't installed via pip (e.g., synthetic test fixtures)
    # or when distribution name equals import name (Stripe). See ADR-023.
    pypi_name: str | None = None


@dataclass
class AdapterResult:
    metadata: SdkMetadata
    diagnostics: list[Diagnostic] = field(default_factory=list)
