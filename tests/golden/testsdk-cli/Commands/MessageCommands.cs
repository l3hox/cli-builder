using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using TestsdkCli.Output;
using TestsdkCli.Auth;
using CliBuilder.TestSdk.Models;
using CliBuilder.TestSdk.Services;

namespace TestsdkCli.Commands;

public static class MessageCommands
{
    private static readonly JsonSerializerOptions _jsonInputOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Command Build(Option<bool> jsonOption, Option<string?> apiKeyOption)
    {
        var command = new Command("message", null);


        // send
        {
            var cmd = new Command("send", null);

            var modelOption = new Option<string>(
                "--model",
                null)
            { IsRequired = false };

            cmd.AddOption(modelOption);

            var temperatureOption = new Option<float?>(
                "--temperature",
                null)
            { IsRequired = false };

            cmd.AddOption(temperatureOption);

            var jsonInputOption = new Option<string?>("--json-input", "Full input as JSON. Flat flags override individual properties.");
            cmd.AddOption(jsonInputOption);

            cmd.SetHandler(async (InvocationContext ctx) =>
            {
                try
                {

                    // Resolve credential (exit code 2 on auth failure)
                    string credential;
                    try
                    {
                        credential = AuthHandler.Resolve(ctx.ParseResult.GetValueForOption(apiKeyOption));
                    }
                    catch (InvalidOperationException authEx)
                    {
                        var authError = new { error = new { code = "auth_error", message = authEx.Message } };
                        Console.Error.WriteLine(JsonSerializer.Serialize(authError));
                        ctx.ExitCode = 2;
                        return;
                    }


                    // Read parameter values

                    var modelValue = ctx.ParseResult.GetValueForOption(modelOption);

                    var temperatureValue = ctx.ParseResult.GetValueForOption(temperatureOption);


                    // Flat flags take precedence; --json-input fills remaining nulls
                    var jsonInputValue = ctx.ParseResult.GetValueForOption(jsonInputOption);


                    // SDK call: MessageClient.SendAsync

                    var client = new MessageClient(credential);

                    // Parse --json-input once for direct param extraction
                    System.Text.Json.JsonDocument? _jsonInputDoc = null;
                    if (jsonInputValue is not null)
                    {
                        try
                        {
                            _jsonInputDoc = System.Text.Json.JsonDocument.Parse(jsonInputValue);
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            var jsonError = new { error = new { code = "json_input_error", message = ex.Message } };
                            Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }

                    List<Message> messagesValue = default!;
                    if (_jsonInputDoc is not null
                        && _jsonInputDoc.RootElement.TryGetProperty("messages", out var _messagesProp))
                    {
                        try
                        {
                            messagesValue = JsonSerializer.Deserialize<List<Message>>(_messagesProp.GetRawText(), _jsonInputOptions)!;
                        }
                        catch (JsonException ex)
                        {
                            var jsonError = new { error = new { code = "json_input_error", message = $"Failed to deserialize 'messages': {ex.Message}" } };
                            Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }

                    if (messagesValue is null)
                    {
                        var missingError = new { error = new { code = "missing_required_param",
                            message = "Required parameter 'messages' must be provided via --json-input" } };
                        Console.Error.WriteLine(JsonSerializer.Serialize(missingError));
                        ctx.ExitCode = 1;
                        return;
                    }

                    var sendMessageOptions = new SendMessageOptions();

                    if (jsonInputValue is not null)
                    {
                        try
                        {
                            sendMessageOptions = JsonSerializer.Deserialize<SendMessageOptions>(jsonInputValue, _jsonInputOptions) ?? sendMessageOptions;
                        }
                        catch (JsonException ex)
                        {
                            var jsonError = new { error = new { code = "json_input_error", message = ex.Message } };
                            Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }

                    if (modelValue is not null)
                        sendMessageOptions.Model = modelValue;

                    if (temperatureValue is not null)
                        sendMessageOptions.Temperature = temperatureValue;

                    var result = (object)await client.SendAsync(messagesValue, sendMessageOptions);


                    // Format output
                    var useJson = ctx.ParseResult.GetValueForOption(jsonOption);
                    if (useJson)
                        JsonFormatter.Write(result);
                    else
                        TableFormatter.Write(result);


                    ctx.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    var errorMessage = AuthHandler.SanitizeMessage(ex.Message);
                    var error = new { error = new { code = "sdk_error", message = errorMessage } };
                    Console.Error.WriteLine(JsonSerializer.Serialize(error));
                    ctx.ExitCode = 3;
                }
            });
            command.AddCommand(cmd);
        }


        // batch
        {
            var cmd = new Command("batch", null);

            var jsonInputOption = new Option<string?>("--json-input", "Full input as JSON. Flat flags override individual properties.");
            cmd.AddOption(jsonInputOption);

            cmd.SetHandler(async (InvocationContext ctx) =>
            {
                try
                {

                    // Resolve credential (exit code 2 on auth failure)
                    string credential;
                    try
                    {
                        credential = AuthHandler.Resolve(ctx.ParseResult.GetValueForOption(apiKeyOption));
                    }
                    catch (InvalidOperationException authEx)
                    {
                        var authError = new { error = new { code = "auth_error", message = authEx.Message } };
                        Console.Error.WriteLine(JsonSerializer.Serialize(authError));
                        ctx.ExitCode = 2;
                        return;
                    }


                    // Read parameter values


                    // Flat flags take precedence; --json-input fills remaining nulls
                    var jsonInputValue = ctx.ParseResult.GetValueForOption(jsonInputOption);


                    // SDK call: MessageClient.BatchAsync

                    var client = new MessageClient(credential);

                    // Parse --json-input once for direct param extraction
                    System.Text.Json.JsonDocument? _jsonInputDoc = null;
                    if (jsonInputValue is not null)
                    {
                        try
                        {
                            _jsonInputDoc = System.Text.Json.JsonDocument.Parse(jsonInputValue);
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            var jsonError = new { error = new { code = "json_input_error", message = ex.Message } };
                            Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }

                    List<string> idsValue = default!;
                    if (_jsonInputDoc is not null
                        && _jsonInputDoc.RootElement.TryGetProperty("ids", out var _idsProp))
                    {
                        try
                        {
                            idsValue = JsonSerializer.Deserialize<List<string>>(_idsProp.GetRawText(), _jsonInputOptions)!;
                        }
                        catch (JsonException ex)
                        {
                            var jsonError = new { error = new { code = "json_input_error", message = $"Failed to deserialize 'ids': {ex.Message}" } };
                            Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }

                    if (idsValue is null)
                    {
                        var missingError = new { error = new { code = "missing_required_param",
                            message = "Required parameter 'ids' must be provided via --json-input" } };
                        Console.Error.WriteLine(JsonSerializer.Serialize(missingError));
                        ctx.ExitCode = 1;
                        return;
                    }

                    var result = (object)await client.BatchAsync(idsValue);


                    // Format output
                    var useJson = ctx.ParseResult.GetValueForOption(jsonOption);
                    if (useJson)
                        JsonFormatter.Write(result);
                    else
                        TableFormatter.Write(result);


                    ctx.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    var errorMessage = AuthHandler.SanitizeMessage(ex.Message);
                    var error = new { error = new { code = "sdk_error", message = errorMessage } };
                    Console.Error.WriteLine(JsonSerializer.Serialize(error));
                    ctx.ExitCode = 3;
                }
            });
            command.AddCommand(cmd);
        }


        return command;
    }
}
