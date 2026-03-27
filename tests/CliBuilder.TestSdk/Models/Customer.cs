namespace CliBuilder.TestSdk.Models;

public class Customer
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Name { get; set; }
    public CustomerStatus Status { get; set; }
    public Address? Address { get; set; }
}

public enum CustomerStatus { Active, Inactive, Suspended }

public class Address
{
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
}
