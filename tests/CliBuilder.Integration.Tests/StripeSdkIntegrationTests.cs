using System.Text.Json;
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;

namespace CliBuilder.Integration.Tests;

public class StripeSdkIntegrationTests
{
    private static readonly string StripeAssemblyPath = GetStripeAssemblyPath();
    private static readonly string FixturesDir = GetFixturesDir();

    private static string GetStripeAssemblyPath()
    {
        // Stripe.net is a PackageReference — its DLL lands in the test output directory
        var testDir = Path.GetDirectoryName(typeof(StripeSdkIntegrationTests).Assembly.Location)!;
        var sdkPath = Path.Combine(testDir, "Stripe.net.dll");

        if (!File.Exists(sdkPath))
            throw new InvalidOperationException(
                $"Stripe.net.dll not found at: {sdkPath}. Ensure the project has the Stripe.net NuGet package.");

        return sdkPath;
    }

    private static string GetFixturesDir()
    {
        var testDir = Path.GetDirectoryName(typeof(StripeSdkIntegrationTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "tests", "fixtures");
    }

    private AdapterResult ExtractStripe()
    {
        var adapter = new DotNetAdapter();
        return adapter.Extract(new AdapterOptions(StripeAssemblyPath));
    }

    [Fact]
    public void ExtractStripe_ProducesResources()
    {
        var result = ExtractStripe();
        Assert.NotEmpty(result.Metadata.Resources);
        // Stripe has 100+ service classes
        Assert.True(result.Metadata.Resources.Count > 50,
            $"Expected 50+ resources from Stripe SDK, got {result.Metadata.Resources.Count}");
    }

    [Fact]
    public void ExtractStripe_DetectsAuthPattern()
    {
        var result = ExtractStripe();
        Assert.NotEmpty(result.Metadata.AuthPatterns);
    }

    [Fact]
    public void ExtractStripe_HasPaymentIntentResource()
    {
        var result = ExtractStripe();
        Assert.Contains(result.Metadata.Resources, r => r.Name == "payment-intent");
    }

    [Fact]
    public void ExtractStripe_PaymentIntentHasCrudOperations()
    {
        var result = ExtractStripe();
        var paymentIntent = result.Metadata.Resources.First(r => r.Name == "payment-intent");
        Assert.Contains(paymentIntent.Operations, o => o.Name == "create");
        Assert.Contains(paymentIntent.Operations, o => o.Name == "get");
        Assert.Contains(paymentIntent.Operations, o => o.Name == "list");
    }

    [Fact]
    public void ExtractStripe_ReportsNounCollisions()
    {
        // Stripe has duplicate service names across namespaces (e.g., CustomerService in
        // Stripe and Stripe.Tax). These are reported as CB202 and excluded.
        var result = ExtractStripe();
        Assert.Contains(result.Diagnostics, d => d.Code == "CB202");
    }

    [Fact]
    public void ExtractStripe_WritesFixture()
    {
        var result = ExtractStripe();
        var json = JsonSerializer.Serialize(result, SdkMetadataJson.Options);
        var fixturePath = Path.Combine(FixturesDir, "stripe-metadata.json");
        Directory.CreateDirectory(FixturesDir);
        File.WriteAllText(fixturePath, json);

        Assert.True(File.Exists(fixturePath));
        var parsed = JsonSerializer.Deserialize<AdapterResult>(File.ReadAllText(fixturePath), SdkMetadataJson.Options);
        Assert.NotNull(parsed);
        Assert.Equal(result.Metadata.Resources.Count, parsed.Metadata.Resources.Count);
    }

    [Fact]
    public void ExtractStripe_DetectsStaticAuthSetup()
    {
        var result = ExtractStripe();
        Assert.Equal("Stripe.StripeConfiguration.ApiKey", result.Metadata.StaticAuthSetup);
    }

    [Fact]
    public void ExtractStripe_ServicesHaveParameterlessCtor()
    {
        var result = ExtractStripe();
        var paymentIntent = result.Metadata.Resources.First(r => r.Name == "payment-intent");
        Assert.True(paymentIntent.HasParameterlessCtor);
    }
}
