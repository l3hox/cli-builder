using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using TestsdkCli.Output;
using TestsdkCli.Auth;
using CliBuilder.TestSdk.Services;

namespace TestsdkCli.Commands;

public static class ShippingServiceCommands
{
    private static readonly JsonSerializerOptions _jsonInputOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Command Build(Option<bool> jsonOption, Option<string?> apiKeyOption)
    {
        var command = new Command("shipping-service", null);


        // track
        {
            var cmd = new Command("track", null);

            var trackingIdOption = new Option<string>(
                "--tracking-id",
                null)
            { IsRequired = true };

            cmd.AddOption(trackingIdOption);

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

                    var trackingIdValue = ctx.ParseResult.GetValueForOption(trackingIdOption);

                    // SDK client wiring not available (source class name unknown)
                    await Task.CompletedTask;
                    var result = new Dictionary<string, object?>
                    {
                        ["command"] = "shipping-service track",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["trackingId"] = trackingIdValue

                        }
                    };

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
