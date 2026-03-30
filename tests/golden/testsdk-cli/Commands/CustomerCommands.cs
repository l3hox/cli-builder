using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using TestsdkCli.Output;
using TestsdkCli.Auth;
using CliBuilder.TestSdk.Services;

namespace TestsdkCli.Commands;

public static class CustomerCommands
{
    public static Command Build(Option<bool> jsonOption, Option<string?> apiKeyOption)
    {
        var command = new Command("customer", null);


        // create
        {
            var cmd = new Command("create", null);

            var emailOption = new Option<string>(
                "--email",
                null)
            { IsRequired = true };


            cmd.AddOption(emailOption);

            var preferredContactOption = new Option<bool>(
                "--preferred-contact",
                null)
            { IsRequired = true };


            cmd.AddOption(preferredContactOption);

            var creditLimitOption = new Option<int?>(
                "--credit-limit",
                null)
            { IsRequired = false };


            cmd.AddOption(creditLimitOption);

            var currencyOption = new Option<string>(
                "--currency",
                null)
            { IsRequired = false };


            cmd.AddOption(currencyOption);

            var descriptionOption = new Option<string>(
                "--description",
                null)
            { IsRequired = false };


            cmd.AddOption(descriptionOption);

            var initialStatusOption = new Option<string>(
                "--initial-status",
                null)
            { IsRequired = false };


            initialStatusOption.FromAmong("Active", "Inactive", "Suspended");

            cmd.AddOption(initialStatusOption);

            var localeOption = new Option<string>(
                "--locale",
                null)
            { IsRequired = false };


            cmd.AddOption(localeOption);

            var nameOption = new Option<string>(
                "--name",
                null)
            { IsRequired = false };


            cmd.AddOption(nameOption);

            var phoneOption = new Option<string>(
                "--phone",
                null)
            { IsRequired = false };


            cmd.AddOption(phoneOption);

            var taxIdOption = new Option<string>(
                "--tax-id",
                null)
            { IsRequired = false };


            cmd.AddOption(taxIdOption);

            var idempotencyKeyOption = new Option<string>(
                "--idempotency-key",
                null)
            { IsRequired = false };


            cmd.AddOption(idempotencyKeyOption);

            var timeoutOption = new Option<string>(
                "--timeout",
                null)
            { IsRequired = false };


            cmd.AddOption(timeoutOption);


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

                    var emailValue = ctx.ParseResult.GetValueForOption(emailOption);

                    var preferredContactValue = ctx.ParseResult.GetValueForOption(preferredContactOption);

                    var creditLimitValue = ctx.ParseResult.GetValueForOption(creditLimitOption);

                    var currencyValue = ctx.ParseResult.GetValueForOption(currencyOption);

                    var descriptionValue = ctx.ParseResult.GetValueForOption(descriptionOption);

                    var initialStatusValue = ctx.ParseResult.GetValueForOption(initialStatusOption);

                    var localeValue = ctx.ParseResult.GetValueForOption(localeOption);

                    var nameValue = ctx.ParseResult.GetValueForOption(nameOption);

                    var phoneValue = ctx.ParseResult.GetValueForOption(phoneOption);

                    var taxIdValue = ctx.ParseResult.GetValueForOption(taxIdOption);

                    var idempotencyKeyValue = ctx.ParseResult.GetValueForOption(idempotencyKeyOption);

                    var timeoutValue = ctx.ParseResult.GetValueForOption(timeoutOption);




                    // SDK call: CustomerService.CreateAsync
                    // var client = new CustomerService(credential);

                    // var sdkOptions = new CreateCustomerOptions();

                    // sdkOptions.Email = emailValue;

                    // sdkOptions.PreferredContact = preferredContactValue;

                    // sdkOptions.CreditLimit = creditLimitValue;

                    // sdkOptions.Currency = currencyValue;

                    // sdkOptions.Description = descriptionValue;

                    // sdkOptions.InitialStatus = initialStatusValue;

                    // sdkOptions.Locale = localeValue;

                    // sdkOptions.Name = nameValue;

                    // sdkOptions.Phone = phoneValue;

                    // sdkOptions.TaxId = taxIdValue;

                    // sdkOptions.IdempotencyKey = idempotencyKeyValue;

                    // sdkOptions.Timeout = timeoutValue;



                    // var result = await client.CreateAsync(sdkOptions);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "customer create",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["Email"] = emailValue,

                            ["PreferredContact"] = preferredContactValue,

                            ["CreditLimit"] = creditLimitValue,

                            ["Currency"] = currencyValue,

                            ["Description"] = descriptionValue,

                            ["InitialStatus"] = initialStatusValue,

                            ["Locale"] = localeValue,

                            ["Name"] = nameValue,

                            ["Phone"] = phoneValue,

                            ["TaxId"] = taxIdValue,

                            ["IdempotencyKey"] = idempotencyKeyValue,

                            ["Timeout"] = timeoutValue

                        },
                        ["authenticated"] = true
                    };

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


        // get
        {
            var cmd = new Command("get", null);

            var idOption = new Option<string>(
                "--id",
                null)
            { IsRequired = true };


            cmd.AddOption(idOption);


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

                    var idValue = ctx.ParseResult.GetValueForOption(idOption);




                    // SDK call: CustomerService.GetAsync
                    // var client = new CustomerService(credential);


                    // var result = await client.GetAsync(idValue);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "customer get",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["id"] = idValue

                        },
                        ["authenticated"] = true
                    };

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


        // list
        {
            var cmd = new Command("list", null);

            var limitOption = new Option<int>(
                "--limit",
                null)
            { IsRequired = false };


            cmd.AddOption(limitOption);

            var cursorOption = new Option<string>(
                "--cursor",
                null)
            { IsRequired = false };


            cmd.AddOption(cursorOption);


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

                    var limitValue = ctx.ParseResult.GetValueForOption(limitOption);

                    var cursorValue = ctx.ParseResult.GetValueForOption(cursorOption);




                    // SDK call: CustomerService.ListAsync
                    // var client = new CustomerService(credential);


                    // var result = await client.ListAsync(limitValue, cursorValue);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "customer list",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["limit"] = limitValue,

                            ["cursor"] = cursorValue

                        },
                        ["authenticated"] = true
                    };

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


        // delete
        {
            var cmd = new Command("delete", null);

            var idOption = new Option<string>(
                "--id",
                null)
            { IsRequired = true };


            cmd.AddOption(idOption);


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

                    var idValue = ctx.ParseResult.GetValueForOption(idOption);




                    // SDK call: CustomerService.DeleteAsync
                    // var client = new CustomerService(credential);


                    // var result = await client.DeleteAsync(idValue);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "customer delete",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["id"] = idValue

                        },
                        ["authenticated"] = true
                    };

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


        // stream [streaming]
        {
            var cmd = new Command("stream", null);


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




                    // SDK call: CustomerService.StreamAsync
                    // var client = new CustomerService(credential);


                    // var result = await client.StreamAsync();
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "customer stream",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                        },
                        ["authenticated"] = true
                    };

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


        // get-metadata
        {
            var cmd = new Command("get-metadata", null);

            var idOption = new Option<string>(
                "--id",
                null)
            { IsRequired = true };


            cmd.AddOption(idOption);


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

                    var idValue = ctx.ParseResult.GetValueForOption(idOption);




                    // SDK call: CustomerService.GetMetadataAsync
                    // var client = new CustomerService(credential);


                    // var result = await client.GetMetadataAsync(idValue);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "customer get-metadata",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["id"] = idValue

                        },
                        ["authenticated"] = true
                    };

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
