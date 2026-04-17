using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CliBuilder.Tests;

/// <summary>
/// Single source of truth for repo-root-relative paths in .NET tests.
/// Auto-included in every <c>IsTestProject</c> assembly by <c>dotnet/Directory.Build.props</c>.
/// </summary>
internal static class TestPaths
{
    public static string RepoRoot { get; }

    /// <summary>Build configuration this test assembly was compiled under (Debug or Release).</summary>
    public static string Configuration { get; }

    static TestPaths()
    {
        var repoRoot = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value
            ?? throw new InvalidOperationException(
                "RepoRoot AssemblyMetadata missing — dotnet/Directory.Build.props is not being applied to this project.");

        var gitPath = Path.Combine(repoRoot, ".git");
        if (!Directory.Exists(gitPath) && !File.Exists(gitPath))
            throw new InvalidOperationException(
                $"Repo root resolution broke — expected .git at {gitPath}, but none was found. " +
                "Check dotnet/Directory.Build.props RepoRoot property.");

        RepoRoot = repoRoot;

        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        Configuration = testDir.Contains(Path.Combine("bin", "Release")) ? "Release" : "Debug";
    }

    /// <summary>Shared JSON metadata fixtures — language-agnostic, lives at repo-root <c>tests/fixtures</c>.</summary>
    public static string Fixtures => Path.Combine(RepoRoot, "tests", "fixtures");

    /// <summary>.NET test projects root.</summary>
    public static string DotnetTests => Path.Combine(RepoRoot, "dotnet", "tests");

    /// <summary>Golden file directory used by generator snapshot tests.</summary>
    public static string Golden => Path.Combine(DotnetTests, "golden");

    /// <summary>CliBuilder.TestSdk built assembly path (respects current Configuration).</summary>
    public static string TestSdkAssembly =>
        Path.Combine(DotnetTests, "CliBuilder.TestSdk", "bin", Configuration, "net8.0", "CliBuilder.TestSdk.dll");

    /// <summary>CliBuilder.TestSdk .csproj path (used when generating with ProjectReference).</summary>
    public static string TestSdkCsproj =>
        Path.Combine(DotnetTests, "CliBuilder.TestSdk", "CliBuilder.TestSdk.csproj");
}
