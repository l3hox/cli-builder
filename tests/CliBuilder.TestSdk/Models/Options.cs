namespace CliBuilder.TestSdk.Models;

// Exactly 10 scalar properties — boundary: all should flatten
public class CreateCustomerOptions
{
    public string Email { get; set; } = "";
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Description { get; set; }
    public string? Currency { get; set; }
    public string? TaxId { get; set; }
    public string? Locale { get; set; }
    public bool PreferredContact { get; set; }
    public int? CreditLimit { get; set; }
    public CustomerStatus? InitialStatus { get; set; }
}

// 15 scalar properties — boundary: 10 flat + --json-input
public class CreateOrderOptions
{
    public string CustomerId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public bool IsPriority { get; set; }
    public int Quantity { get; set; }
    public string? CouponCode { get; set; }
    public string? ShippingMethod { get; set; }
    public decimal? TaxRate { get; set; }
    public string? Region { get; set; }
    public bool GiftWrap { get; set; }
    public string? GiftMessage { get; set; }
}

// Contains a nested object property — always routes to --json-input
public class NestedOptions
{
    public string Name { get; set; } = "";
    public Address? ShippingAddress { get; set; }
}

// Sanitization edge cases — parameter names that are C# keywords
public class SanitizationOptions
{
    public string @class { get; set; } = "";
    public string @event { get; set; } = "";
    public string NormalParam { get; set; } = "";
}
