//! Python language profile — implements LanguageProfile for Python CLI generation.

use cli_builder_core::generator_model::LanguageProfile;
use cli_builder_core::models::{TypeKind, TypeRef};

use crate::python_keywords;

pub struct PythonProfile;

impl LanguageProfile for PythonProfile {
    fn map_cli_type(&self, type_ref: &TypeRef, for_cli_param: bool) -> String {
        match type_ref.kind {
            TypeKind::Primitive => self.map_primitive_type(&type_ref.name),
            TypeKind::Enum => "str".to_string(),
            TypeKind::Class if for_cli_param => "str".to_string(),
            TypeKind::Class => type_ref.name.clone(),
            TypeKind::Array if for_cli_param => "str".to_string(),
            TypeKind::Array => {
                let elem = type_ref
                    .element_type
                    .as_ref()
                    .map(|et| self.map_cli_type(et, false))
                    .unwrap_or_else(|| "object".to_string());
                format!("list[{}]", elem)
            }
            TypeKind::Dictionary => "str".to_string(),
            TypeKind::Generic if for_cli_param => "str".to_string(),
            TypeKind::Generic => type_ref.name.clone(),
            TypeKind::Other => "str".to_string(),
        }
    }

    fn map_primitive_type(&self, name: &str) -> String {
        match name {
            "str" | "string" | "String" => "str",
            "int" | "Int32" | "long" | "Int64" | "short" | "Int16" | "byte" | "Byte" => "int",
            "float" | "Single" | "double" | "Double" | "decimal" | "Decimal" => "float",
            "bool" | "Boolean" => "bool",
            "void" | "Void" | "None" | "NoneType" => "None",
            "TimeSpan" | "DateTime" | "DateTimeOffset" | "Guid" => "str",
            _ => "str",
        }
        .to_string()
    }

    fn build_deserialization_type_name(&self, type_ref: &TypeRef) -> String {
        match type_ref.kind {
            TypeKind::Array => {
                let elem = type_ref
                    .element_type
                    .as_ref()
                    .map(|et| et.name.as_str())
                    .or_else(|| {
                        type_ref
                            .generic_arguments
                            .as_ref()
                            .and_then(|gas| gas.first())
                            .map(|ga| ga.name.as_str())
                    })
                    .unwrap_or("object");
                format!("list[{}]", elem)
            }
            TypeKind::Dictionary => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if gas.len() == 2 {
                        return format!("dict[{}, {}]", gas[0].name, gas[1].name);
                    }
                }
                "dict[str, object]".to_string()
            }
            TypeKind::Generic => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if !gas.is_empty() {
                        if type_ref.name.contains("Dictionary") && gas.len() == 2 {
                            return format!("dict[{}, {}]", gas[0].name, gas[1].name);
                        }
                        return format!("list[{}]", gas[0].name);
                    }
                }
                type_ref.name.clone()
            }
            _ => type_ref.name.clone(),
        }
    }

    fn is_keyword(&self, name: &str) -> bool {
        python_keywords::is_keyword(name)
    }

    fn is_boilerplate_name(&self, name: &str) -> bool {
        python_keywords::is_boilerplate_name(name)
    }

    fn is_binary_type(&self, name: &str) -> bool {
        matches!(
            name,
            "bytes" | "bytearray" | "BinaryIO" | "IO"
                | "BinaryContent" | "BinaryData" | "Stream"
                | "ReadOnlyMemory" | "ReadOnlySpan"
        )
    }

    fn is_infrastructure_type(&self, name: &str) -> bool {
        name == "RequestOptions"
            || name == "CancellationToken"
            || name.ends_with("ClientOptions")
            || name.ends_with("ClientSettings")
    }

    fn is_unwirable_return_type(&self, name: &str) -> bool {
        matches!(
            name,
            "AsyncCollectionResult" | "CollectionResult" | "Uri" | "Stream"
                | "AsyncIterator" | "Iterator" | "Generator"
        ) || name.len() == 1
            || name.ends_with("Client")
            || name.ends_with("Service")
            || name.ends_with("Api")
            || name.ends_with("ClientSettings")
            || name.ends_with("Options")
    }
}
