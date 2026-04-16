//! C# language profile — implements LanguageProfile for C# CLI generation.

use cli_builder_core::generator_model::LanguageProfile;
use cli_builder_core::models::{TypeKind, TypeRef};

use crate::csharp_keywords;

pub struct CSharpProfile;

impl LanguageProfile for CSharpProfile {
    fn map_cli_type(&self, type_ref: &TypeRef, for_cli_param: bool) -> String {
        let base = match type_ref.kind {
            TypeKind::Primitive => self.map_primitive_type(&type_ref.name),
            TypeKind::Enum => "string".to_string(),
            TypeKind::Class if for_cli_param => "string".to_string(),
            TypeKind::Class => type_ref.name.clone(),
            TypeKind::Array if for_cli_param => "string".to_string(),
            TypeKind::Array => {
                let elem = type_ref
                    .element_type
                    .as_ref()
                    .map(|et| self.map_cli_type(et, false))
                    .unwrap_or_else(|| "object".to_string());
                format!("{}[]", elem)
            }
            TypeKind::Dictionary => "string".to_string(),
            TypeKind::Generic if for_cli_param => "string".to_string(),
            TypeKind::Generic => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if !gas.is_empty() {
                        let args: Vec<String> =
                            gas.iter().map(|t| self.map_cli_type(t, false)).collect();
                        return format!("{}<{}>", type_ref.name, args.join(", "));
                    }
                }
                type_ref.name.clone()
            }
            TypeKind::Other => "object".to_string(),
        };

        // Append ? for nullable value types (string/object are reference types — already nullable)
        if type_ref.is_nullable && !matches!(base.as_str(), "string" | "object" | "void") {
            format!("{}?", base)
        } else {
            base
        }
    }

    fn map_primitive_type(&self, name: &str) -> String {
        match name {
            "string" | "String" => "string",
            "int" | "Int32" => "int",
            "long" | "Int64" => "long",
            "bool" | "Boolean" => "bool",
            "double" | "Double" => "double",
            "float" | "Single" => "float",
            "decimal" | "Decimal" => "decimal",
            "byte" | "Byte" => "byte",
            "short" | "Int16" => "short",
            "TimeSpan" | "DateTime" | "DateTimeOffset" | "Guid" => "string",
            "void" | "Void" => "void",
            _ => "string",
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
                format!("{}[]", elem)
            }
            TypeKind::Dictionary => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if gas.len() == 2 {
                        return format!("Dictionary<{}, {}>", gas[0].name, gas[1].name);
                    }
                }
                "Dictionary<string, object>".to_string()
            }
            TypeKind::Generic => {
                if let Some(ref gas) = type_ref.generic_arguments {
                    if !gas.is_empty() {
                        if type_ref.name.contains("Dictionary") && gas.len() == 2 {
                            return format!("Dictionary<{}, {}>", gas[0].name, gas[1].name);
                        }
                        return format!("List<{}>", gas[0].name);
                    }
                }
                type_ref.name.clone()
            }
            _ => type_ref.name.clone(),
        }
    }

    fn is_keyword(&self, name: &str) -> bool {
        csharp_keywords::is_keyword(name)
    }

    fn is_boilerplate_name(&self, name: &str) -> bool {
        csharp_keywords::is_boilerplate_name(name)
    }

    fn is_binary_type(&self, name: &str) -> bool {
        matches!(
            name,
            "BinaryContent" | "BinaryData" | "Stream" | "ReadOnlyMemory" | "ReadOnlySpan"
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
        ) || name.len() == 1
            || name.ends_with("Client")
            || name.ends_with("Service")
            || name.ends_with("Api")
            || name.ends_with("ClientSettings")
            || name.ends_with("Options")
            || name.ends_with("Response")
            || name.ends_with("Notification")
    }
}
