using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using TestsdkCli.Output;
using TestsdkCli.Auth;
using CliBuilder.TestSdk.Services;

namespace TestsdkCli.Commands;

public static class OrderCommands
{
    public static Command Build(Option<bool> jsonOption, Option<string?> apiKeyOption)
    {
        var command = new Command("order", null);


        // create
        {
            var cmd = new Command("create", null);

            var currencyOption = new Option<string>(
                "--currency",
                null)
            { IsRequired = true };


            cmd.AddOption(currencyOption);

            var customerIdOption = new Option<string>(
                "--customer-id",
                null)
            { IsRequired = true };


            cmd.AddOption(customerIdOption);

            var productIdOption = new Option<string>(
                "--product-id",
                null)
            { IsRequired = true };


            cmd.AddOption(productIdOption);

            var amountOption = new Option<decimal?>(
                "--amount",
                null)
            { IsRequired = false };


            cmd.AddOption(amountOption);

            var couponCodeOption = new Option<string>(
                "--coupon-code",
                null)
            { IsRequired = false };


            cmd.AddOption(couponCodeOption);

            var descriptionOption = new Option<string>(
                "--description",
                null)
            { IsRequired = false };


            cmd.AddOption(descriptionOption);

            var giftMessageOption = new Option<string>(
                "--gift-message",
                null)
            { IsRequired = false };


            cmd.AddOption(giftMessageOption);

            var giftWrapOption = new Option<bool?>(
                "--gift-wrap",
                null)
            { IsRequired = false };


            cmd.AddOption(giftWrapOption);

            var isPriorityOption = new Option<bool?>(
                "--is-priority",
                null)
            { IsRequired = false };


            cmd.AddOption(isPriorityOption);

            var notesOption = new Option<string>(
                "--notes",
                null)
            { IsRequired = false };


            cmd.AddOption(notesOption);


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

                    var currencyValue = ctx.ParseResult.GetValueForOption(currencyOption);

                    var customerIdValue = ctx.ParseResult.GetValueForOption(customerIdOption);

                    var productIdValue = ctx.ParseResult.GetValueForOption(productIdOption);

                    var amountValue = ctx.ParseResult.GetValueForOption(amountOption);

                    var couponCodeValue = ctx.ParseResult.GetValueForOption(couponCodeOption);

                    var descriptionValue = ctx.ParseResult.GetValueForOption(descriptionOption);

                    var giftMessageValue = ctx.ParseResult.GetValueForOption(giftMessageOption);

                    var giftWrapValue = ctx.ParseResult.GetValueForOption(giftWrapOption);

                    var isPriorityValue = ctx.ParseResult.GetValueForOption(isPriorityOption);

                    var notesValue = ctx.ParseResult.GetValueForOption(notesOption);



                    // Flat flags take precedence; --json-input fills remaining nulls
                    var jsonInputValue = ctx.ParseResult.GetValueForOption(jsonInputOption);



                    // SDK call: OrderClient.CreateAsync
                    // var client = new OrderClient(credential);

                    // var sdkOptions = new CreateOrderOptions();

                    // sdkOptions.Currency = currencyValue;

                    // sdkOptions.CustomerId = customerIdValue;

                    // sdkOptions.ProductId = productIdValue;

                    // sdkOptions.Amount = amountValue;

                    // sdkOptions.CouponCode = couponCodeValue;

                    // sdkOptions.Description = descriptionValue;

                    // sdkOptions.GiftMessage = giftMessageValue;

                    // sdkOptions.GiftWrap = giftWrapValue;

                    // sdkOptions.IsPriority = isPriorityValue;

                    // sdkOptions.Notes = notesValue;



                    // var result = await client.CreateAsync(sdkOptions);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "order create",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["Currency"] = currencyValue,

                            ["CustomerId"] = customerIdValue,

                            ["ProductId"] = productIdValue,

                            ["Amount"] = amountValue,

                            ["CouponCode"] = couponCodeValue,

                            ["Description"] = descriptionValue,

                            ["GiftMessage"] = giftMessageValue,

                            ["GiftWrap"] = giftWrapValue,

                            ["IsPriority"] = isPriorityValue,

                            ["Notes"] = notesValue

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




                    // SDK call: OrderClient.GetAsync
                    // var client = new OrderClient(credential);


                    // var result = await client.GetAsync(idValue);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "order get",
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


        // update
        {
            var cmd = new Command("update", null);

            var nameOption = new Option<string>(
                "--name",
                null)
            { IsRequired = true };


            cmd.AddOption(nameOption);


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

                    var nameValue = ctx.ParseResult.GetValueForOption(nameOption);



                    // Flat flags take precedence; --json-input fills remaining nulls
                    var jsonInputValue = ctx.ParseResult.GetValueForOption(jsonInputOption);



                    // SDK call: OrderClient.UpdateAsync
                    // var client = new OrderClient(credential);

                    // var sdkOptions = new NestedOptions();

                    // sdkOptions.Name = nameValue;



                    // var result = await client.UpdateAsync(sdkOptions);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "order update",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["Name"] = nameValue

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


        // process
        {
            var cmd = new Command("process", null);

            var classValueOption = new Option<string>(
                "--class-value",
                null)
            { IsRequired = true };


            cmd.AddOption(classValueOption);

            var eventValueOption = new Option<string>(
                "--event-value",
                null)
            { IsRequired = true };


            cmd.AddOption(eventValueOption);

            var normalParamOption = new Option<string>(
                "--normal-param",
                null)
            { IsRequired = true };


            cmd.AddOption(normalParamOption);


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

                    var classValueValue = ctx.ParseResult.GetValueForOption(classValueOption);

                    var eventValueValue = ctx.ParseResult.GetValueForOption(eventValueOption);

                    var normalParamValue = ctx.ParseResult.GetValueForOption(normalParamOption);




                    // SDK call: OrderClient.ProcessAsync
                    // var client = new OrderClient(credential);

                    // var sdkOptions = new SanitizationOptions();

                    // sdkOptions.@class = classValueValue;

                    // sdkOptions.@event = eventValueValue;

                    // sdkOptions.NormalParam = normalParamValue;



                    // var result = await client.ProcessAsync(sdkOptions);
                    await Task.CompletedTask;
                    var result = (object)new Dictionary<string, object?>
                    {
                        ["command"] = "order process",
                        ["parameters"] = new Dictionary<string, object?>
                        {

                            ["@class"] = classValueValue,

                            ["@event"] = eventValueValue,

                            ["NormalParam"] = normalParamValue

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
