using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using TestsdkCli.Output;
using TestsdkCli.Auth;
using CliBuilder.TestSdk.Models;
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

            var amountOption = new Option<decimal>(
                "--amount",
                null)
            { IsRequired = true };


            cmd.AddOption(amountOption);

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

            var giftWrapOption = new Option<bool>(
                "--gift-wrap",
                null)
            { IsRequired = true };


            cmd.AddOption(giftWrapOption);

            var isPriorityOption = new Option<bool>(
                "--is-priority",
                null)
            { IsRequired = true };


            cmd.AddOption(isPriorityOption);

            var productIdOption = new Option<string>(
                "--product-id",
                null)
            { IsRequired = true };


            cmd.AddOption(productIdOption);

            var quantityOption = new Option<int>(
                "--quantity",
                null)
            { IsRequired = true };


            cmd.AddOption(quantityOption);

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

                    var amountValue = ctx.ParseResult.GetValueForOption(amountOption);

                    var currencyValue = ctx.ParseResult.GetValueForOption(currencyOption);

                    var customerIdValue = ctx.ParseResult.GetValueForOption(customerIdOption);

                    var giftWrapValue = ctx.ParseResult.GetValueForOption(giftWrapOption);

                    var isPriorityValue = ctx.ParseResult.GetValueForOption(isPriorityOption);

                    var productIdValue = ctx.ParseResult.GetValueForOption(productIdOption);

                    var quantityValue = ctx.ParseResult.GetValueForOption(quantityOption);

                    var couponCodeValue = ctx.ParseResult.GetValueForOption(couponCodeOption);

                    var descriptionValue = ctx.ParseResult.GetValueForOption(descriptionOption);

                    var giftMessageValue = ctx.ParseResult.GetValueForOption(giftMessageOption);



                    // Flat flags take precedence; --json-input fills remaining nulls
                    var jsonInputValue = ctx.ParseResult.GetValueForOption(jsonInputOption);



                    // SDK call: OrderClient.CreateAsync
                    var client = new OrderClient(credential);



                    var createOrderOptions = new CreateOrderOptions();


                    createOrderOptions.Amount = amountValue;



                    createOrderOptions.Currency = currencyValue;



                    createOrderOptions.CustomerId = customerIdValue;



                    createOrderOptions.GiftWrap = giftWrapValue;



                    createOrderOptions.IsPriority = isPriorityValue;



                    createOrderOptions.ProductId = productIdValue;



                    createOrderOptions.Quantity = quantityValue;



                    createOrderOptions.CouponCode = couponCodeValue;



                    createOrderOptions.Description = descriptionValue;



                    createOrderOptions.GiftMessage = giftMessageValue;





                    var result = (object)await client.CreateAsync(createOrderOptions);



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
                    var client = new OrderClient(credential);




                    var result = (object)await client.GetAsync(idValue);



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
                    var client = new OrderClient(credential);



                    var nestedOptions = new NestedOptions();


                    nestedOptions.Name = nameValue;





                    var result = (object)await client.UpdateAsync(nestedOptions);



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
                    var client = new OrderClient(credential);



                    var sanitizationOptions = new SanitizationOptions();


                    sanitizationOptions.@class = classValueValue;



                    sanitizationOptions.@event = eventValueValue;



                    sanitizationOptions.NormalParam = normalParamValue;





                    var result = (object)await client.ProcessAsync(sanitizationOptions);



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
