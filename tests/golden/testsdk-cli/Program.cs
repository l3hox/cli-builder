using System.CommandLine;

using TestsdkCli.Commands;


var rootCommand = new RootCommand(@"testsdk-cli — CLI for CliBuilder.TestSdk");

// Global --json option available on all commands
var jsonOption = new Option<bool>("--json", "Output as JSON instead of table format");
rootCommand.AddGlobalOption(jsonOption);


// Global --api-key option (last-resort auth, prefer env var)
var apiKeyOption = new Option<string?>("--api-key", "API key (prefer TESTSDK_APIKEY env var instead)");
rootCommand.AddGlobalOption(apiKeyOption);



rootCommand.AddCommand(ShippingServiceCommands.Build(jsonOption, apiKeyOption));


rootCommand.AddCommand(ShippingClientCommands.Build(jsonOption, apiKeyOption));


rootCommand.AddCommand(CustomerCommands.Build(jsonOption, apiKeyOption));


rootCommand.AddCommand(OrderCommands.Build(jsonOption, apiKeyOption));


rootCommand.AddCommand(ProductCommands.Build(jsonOption, apiKeyOption));


rootCommand.AddCommand(SearchCommands.Build(jsonOption, apiKeyOption));


return await rootCommand.InvokeAsync(args);
