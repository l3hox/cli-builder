namespace CliBuilder.TestSdk.Models;

public class Order
{
    public string Id { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Name { get; set; }
    public Address? ShippingAddress { get; set; }
}
