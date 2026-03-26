# Step 5: .NET Reflection Adapter

**Prerequisite:** Step 4 scaffold complete (solution builds, 8 tests pass).
**Output:** `DotNetAdapter` extracts `SdkMetadata` from .NET assemblies. Validated against TestSdk and OpenAI SDK.

Split into 4 phases with checkpoints between each.

---

## Context

Read before implementing:
- [cli-builder-spec.md](../cli-builder-spec.md) — discovery strategy (lines 187-230), metadata model (lines 159-197)
- [docs/design-notes.md](../design-notes.md) — naming conventions, verb collision rules, flattening ordering, operationPattern semantics, identifier validation, diagnostic codes, test SDK manifest
- [docs/ADR.md](../ADR.md) — ADR-001 (reflection), ADR-003 (MetadataLoadContext only), ADR-008 (naming), ADR-013 (package artifacts), ADR-015 (diagnostics)

OpenAI .NET SDK research (informs TestSdk design):
- Classes end in `Client` (not `Service`)
- Methods return `ClientResult<T>`, `AsyncCollectionResult<T>`, `CollectionResult<T>`
- Auth via `ApiKeyCredential` or `string apiKey` constructor params
- Options objects pattern: `ChatCompletionOptions`, `EmbeddingGenerationOptions`, etc.
- All async methods end in `Async`, accept optional `CancellationToken` as last param
- ~15 client classes: ChatClient, EmbeddingClient, ImageClient, AudioClient, etc.

---

## Phase 5A: Build the TestSdk assembly

**Goal:** A small, purpose-built class library that exercises every discovery pattern the adapter must handle. No adapter code yet — just the test fixture.

**Files to create in `tests/CliBuilder.TestSdk/`:**

### `Services/CustomerService.cs`
Standard service class matching `*Service` pattern.
```csharp
namespace CliBuilder.TestSdk.Services;

public class CustomerService
{
    public CustomerService(string apiKey) { }

    public Task<Customer> CreateAsync(CreateCustomerOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Customer> GetAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<List<Customer>> ListAsync(int limit = 10, string? cursor = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Non-async overload — collides with GetAsync after Async stripping
    public Customer Get(string id)
        => throw new NotImplementedException();
}
```

### `Services/OrderClient.cs`
Matches `*Client` pattern (like OpenAI SDK).
```csharp
namespace CliBuilder.TestSdk.Services;

public class OrderClient
{
    public OrderClient(string apiKey) { }

    public Task<ClientResult<Order>> CreateAsync(CreateOrderOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ClientResult<Order>> GetAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
```

### `Services/ProductApi.cs`
Matches `*Api` pattern.
```csharp
namespace CliBuilder.TestSdk.Services;

public class ProductApi
{
    public ProductApi(TokenCredential credential) { }

    public Task<Product> ListAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
```

### `Services/InternalHelper.cs`
Should NOT be discovered (no matching suffix).
```csharp
namespace CliBuilder.TestSdk.Services;

public class InternalHelper
{
    public Task<string> DoWorkAsync() => throw new NotImplementedException();
}
```

### `Services/CustomerApiService.cs`
Noun collision with `CustomerService` — both map to `customer`.
```csharp
namespace CliBuilder.TestSdk.Services;

public class CustomerApiService
{
    public Task<Customer> SearchAsync(string query, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
```

### `Models/Customer.cs`
```csharp
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
```

### `Models/Order.cs`
```csharp
namespace CliBuilder.TestSdk.Models;

public class Order
{
    public string Id { get; set; } = "";
    public decimal Amount { get; set; }
}
```

### `Models/Product.cs`
```csharp
namespace CliBuilder.TestSdk.Models;

public class Product
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
```

### `Models/Options.cs`
Options classes for flattening threshold testing.
```csharp
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
```

### `Models/Auth.cs`
Auth pattern types for detection.
```csharp
namespace CliBuilder.TestSdk.Models;

// Simulates ApiKeyCredential pattern (like OpenAI SDK)
public class TokenCredential
{
    public TokenCredential(string token) { }
}

// Simulates ClientResult<T> pattern (like OpenAI SDK)
public class ClientResult<T>
{
    public T Value { get; set; } = default!;
}
```

### Checkpoint 5A

```bash
dotnet build
```

**Verify:**
- [ ] TestSdk builds with no errors
- [ ] Contains: 3 discoverable service classes, 1 non-discoverable, 1 collision class
- [ ] Contains: options classes at exactly 10 and 15 properties
- [ ] Contains: enum type, nullable params, nested object, generic return types
- [ ] No project references from TestSdk to any other project

---

## Phase 5B: Write adapter tests (TDD — tests first)

**Goal:** Write tests that define expected adapter behavior against TestSdk. All tests will FAIL because the adapter is not yet implemented.

**Files to create in `tests/CliBuilder.Core.Tests/`:**

### `DotNetAdapterTests.cs`

Tests that load the TestSdk assembly and assert on extracted `SdkMetadata`.

The test needs to locate the built TestSdk DLL. Use a helper that finds it relative to the test assembly output.

**Test categories:**

1. **Resource discovery**
   - Discovers `CustomerService` as resource `customer`
   - Discovers `OrderClient` as resource `order`
   - Discovers `ProductApi` as resource `product`
   - Does NOT discover `InternalHelper`
   - Emits error diagnostic `CB202` for `CustomerApiService` collision with `CustomerService`

2. **Operation discovery**
   - `CustomerService.CreateAsync` → operation `create`
   - `CustomerService.GetAsync` → operation `get`
   - `CustomerService.ListAsync` → operation `list`
   - `CustomerService.DeleteAsync` → operation `delete`
   - Verb collision: `CustomerService.Get` + `CustomerService.GetAsync` → diagnostic `CB201`
   - `CancellationToken` parameter is excluded from CLI parameters

3. **Type extraction**
   - `Task<Customer>` unwrapped to `TypeRef(Class, "Customer")`
   - `Task<List<Customer>>` unwrapped to `TypeRef(Generic, "List", [TypeRef(Class, "Customer")])`
   - `Task<bool>` unwrapped to `TypeRef(Primitive, "bool")`
   - `Task<ClientResult<Order>>` unwrapped to `TypeRef(Class, "Order")` (double unwrap)
   - `CustomerStatus` enum → `TypeRef(Enum, "CustomerStatus", EnumValues: ["Active", "Inactive", "Suspended"])`
   - Nullable `string?` → `TypeRef(Primitive, "string", IsNullable: true)`

4. **Auth pattern detection**
   - `CustomerService(string apiKey)` → `AuthPattern(ApiKey, "TESTSDK_API_KEY", "apiKey")`
   - `ProductApi(TokenCredential credential)` → `AuthPattern(BearerToken, ...)`

5. **Naming conventions**
   - Resource names are kebab-case: `CustomerService` → `customer`
   - Operation names are kebab-case: `CreateAsync` → `create`
   - `Async` suffix stripped

### Test infrastructure

Add a reference from `CliBuilder.Core.Tests` to `CliBuilder.Adapter.DotNet` (already done in scaffold).

Add a test helper to locate the TestSdk DLL:
```csharp
private static string GetTestSdkAssemblyPath()
{
    // TestSdk is built as part of the solution but NOT referenced by tests.
    // Find it relative to the test output directory.
    var testDir = Path.GetDirectoryName(typeof(DotNetAdapterTests).Assembly.Location)!;
    var sdkPath = Path.Combine(testDir, "..", "..", "..", "..", "..",
        "tests", "CliBuilder.TestSdk", "bin", "Debug", "net8.0", "CliBuilder.TestSdk.dll");
    return Path.GetFullPath(sdkPath);
}
```

### Checkpoint 5B

```bash
dotnet build
dotnet test  # Expect: 8 old tests PASS, new tests FAIL (NotImplementedException)
```

**Verify:**
- [ ] New tests compile
- [ ] New tests fail with `NotImplementedException` (not compile errors)
- [ ] Original 8 serialization tests still pass
- [ ] Test names clearly describe what they verify

---

## Phase 5C: Implement the adapter

**Goal:** Implement `DotNetAdapter.Extract()` until all tests pass.

**Implementation order (incremental — run tests after each):**

1. **Assembly loading** — `MetadataLoadContext` with `PathAssemblyResolver`
   - Scan assembly directory for sibling DLLs
   - Scan .NET runtime reference assemblies
   - (NuGet cache scan deferred — TestSdk has no NuGet deps)

2. **Service class discovery** — scan for public classes matching `*Service`, `*Client`, `*Api`
   - Filter by: has public methods returning `Task<T>` or similar
   - Map class name to kebab-case resource name, strip suffix

3. **Operation discovery** — for each service class, extract public methods
   - Strip `Async` suffix
   - Map to kebab-case verb name
   - Exclude: non-public, static, inherited from `object`
   - Handle verb collisions (non-overload same name → error diagnostic)
   - Handle overloads (richest parameter set wins)
   - Exclude `CancellationToken` parameters

4. **Type extraction** — build `TypeRef` from reflected types
   - Unwrap `Task<T>`, `ValueTask<T>`
   - Unwrap `ClientResult<T>` (and similar wrappers)
   - Handle generics recursively
   - Detect enums, extract values
   - Detect nullable reference types

5. **Auth detection** — scan constructors for credential-like parameters
   - `string apiKey` → ApiKey
   - `*Credential` types → BearerToken

6. **Diagnostics** — emit diagnostics for all edge cases
   - `CB101` — type skipped
   - `CB201` — verb collision
   - `CB202` — noun collision
   - `CB203` — overload disambiguated
   - `CB204` — identifier sanitized

### Checkpoint 5C

```bash
dotnet test  # All tests pass
```

**Verify:**
- [ ] All adapter tests pass
- [ ] All 8 serialization tests still pass
- [ ] No `AssemblyLoadContext` usage anywhere (grep for it)
- [ ] Diagnostics are emitted for: noun collision, verb collision
- [ ] Resource names are kebab-case
- [ ] Operation names have Async stripped and are kebab-case

---

## Phase 5D: Validate against OpenAI .NET SDK

**Goal:** Run the adapter against the real OpenAI SDK assembly. Not a test — an inspection. Commit the output as a reference fixture.

### Steps

1. Download the OpenAI NuGet package:
   ```bash
   dotnet add tests/CliBuilder.Core.Tests package OpenAI
   ```

2. Write a single integration test (or a small console script) that:
   - Loads `OpenAI.dll` from the NuGet cache
   - Runs `DotNetAdapter.Extract()`
   - Serializes the resulting `SdkMetadata` to JSON
   - Writes it to `tests/fixtures/openai-metadata.json`

3. Inspect the output:
   - Are all ~15 client classes discovered?
   - Are method names correctly mapped to verbs?
   - Are `ClientResult<T>` and `AsyncCollectionResult<T>` unwrapped?
   - Is `ApiKeyCredential` detected as auth pattern?
   - What diagnostics were emitted?

4. Commit the JSON fixture as a reference baseline.

### Checkpoint 5D

**Verify:**
- [ ] `openai-metadata.json` contains resources for at least: chat, embedding, image, audio, model
- [ ] Operations have correct verb names (e.g., `complete-chat`, `generate-embedding`)
- [ ] Return types are unwrapped (no `Task<>` or `ClientResult<>` wrappers in output)
- [ ] Auth pattern detected (`ApiKey` or `BearerToken`)
- [ ] Diagnostics list reviewed — no unexpected errors
- [ ] JSON fixture committed for future regression testing

---

## What this step does NOT include

- CLI generator (step 6)
- Config override support (`cli-builder.json` parsing — deferred to step 6 or later)
- XML doc extraction (nice-to-have, can add after core extraction works)
- NuGet cache scanning for dependency resolution (TestSdk has no NuGet deps; OpenAI validation may need it)
