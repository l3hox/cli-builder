using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using TestsdkCli.Output;
using TestsdkCli.Auth;
using CliBuilder.TestSdk.Models;
using CliBuilder.TestSdk.Services;

namespace TestsdkCli.Commands;

public static class SearchCommands
{
    private static readonly JsonSerializerOptions _jsonInputOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Command Build(Option<bool> jsonOption, Option<string?> apiKeyOption)
    {
        var command = new Command("search", null);

        var indexValueOption = new Option<string>("--index")
        { IsRequired = true };
        command.AddGlobalOption(indexValueOption);


        // search
        {
            var cmd = new Command("search", null);

            var queryOption = new Option<string>(
                "--query",
                null)
            { IsRequired = true };

            cmd.AddOption(queryOption);

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

                    var queryValue = ctx.ParseResult.GetValueForOption(queryOption);


                    var indexValue = ctx.ParseResult.GetValueForOption(indexValueOption);

                    // SDK call: SearchClient.SearchAsync

                    var client = new SearchClient(indexValue, new ApiKeyCredential(credential));

                    var result = (object)await client.SearchAsync(queryValue);


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
