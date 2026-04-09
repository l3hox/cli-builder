using System.CommandLine;
using System.CommandLine.Invocation;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Commands;
using CliBuilder.Generator.CSharp;

var rootCommand = new RootCommand("cli-builder — generates agent-ready CLIs from .NET SDK assemblies");

// generate command
var generateCommand = new Command("generate", "Generate a CLI project from a .NET SDK assembly");

var genAssemblyOption = new Option<string>(
    "--assembly", "Path to the SDK assembly (DLL) to generate a CLI from")
{ IsRequired = true };
var outputOption = new Option<string>(
    "--output", "Output directory for the generated CLI project")
{ IsRequired = true };
var nameOption = new Option<string?>(
    "--name", "CLI name (default: derived from assembly name)");
var overwriteOption = new Option<bool>(
    "--overwrite", "Overwrite existing output directory");

generateCommand.AddOption(genAssemblyOption);
generateCommand.AddOption(outputOption);
generateCommand.AddOption(nameOption);
generateCommand.AddOption(overwriteOption);

generateCommand.SetHandler((InvocationContext ctx) =>
{
    var assemblyPath = ctx.ParseResult.GetValueForOption(genAssemblyOption)!;
    var outputDir = ctx.ParseResult.GetValueForOption(outputOption)!;
    var name = ctx.ParseResult.GetValueForOption(nameOption);
    var overwrite = ctx.ParseResult.GetValueForOption(overwriteOption);

    ctx.ExitCode = GenerateCommand.Execute(
        new DotNetAdapter(), new CSharpCliGenerator(),
        assemblyPath, outputDir, name, overwrite);
});

// inspect command
var inspectCommand = new Command("inspect", "Inspect SDK metadata without generating");

var inspectAssemblyOption = new Option<string>(
    "--assembly", "Path to the SDK assembly (DLL) to inspect")
{ IsRequired = true };
var jsonOption = new Option<bool>(
    "--json", "Output as JSON (default: human-readable summary)");

inspectCommand.AddOption(inspectAssemblyOption);
inspectCommand.AddOption(jsonOption);

inspectCommand.SetHandler((InvocationContext ctx) =>
{
    var assemblyPath = ctx.ParseResult.GetValueForOption(inspectAssemblyOption)!;
    var json = ctx.ParseResult.GetValueForOption(jsonOption);

    ctx.ExitCode = InspectCommand.Execute(
        new DotNetAdapter(), assemblyPath, json);
});

rootCommand.AddCommand(generateCommand);
rootCommand.AddCommand(inspectCommand);

return rootCommand.Invoke(args);
