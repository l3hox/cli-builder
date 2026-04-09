using System.Text.Json.Serialization;

namespace CliBuilder.TestSdk.Models;

[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(SystemMessage), "system")]
public abstract class Message
{
    public string Content { get; set; } = "";
}

public class UserMessage : Message { }

public class SystemMessage : Message { }
