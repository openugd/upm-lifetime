# 03 — `using var scope = lifetime.DefineNested(...)`

The everyday shape for an operation with a beginning and an end.

`Lifetime.Definition` implements `IDisposable`, so `using` / `using var` works. The closing brace ends the
scope and runs everything registered inside it, LIFO — including registrations made by helpers you called
and never looked inside. `Dispose()` is a plain public method, not an explicit interface implementation, so
`using` on a `Definition` local does not box.

## The implicit conversion is a capability downgrade

`Definition` converts implicitly to `Lifetime` (null-safe), so you can pass the `scope` variable straight
into anything that takes a `Lifetime`:

```csharp
using var scope = lifetime.DefineNested("load-level");
var atlas = LoadAtlas(scope, ...);   // LoadAtlas(Lifetime, ...) — no `.Lifetime` needed
```

The conversion only goes one way. Passing `scope` to a helper hands it the *observe and register*
capability and keeps the *terminate* capability at home — the split from sample 01, enforced for free at
every call boundary.

## What to look at

`BoundedOperationSample.cs` — `LoadLevel` opens one scope and never calls `Terminate()`. Returning from the
method (or throwing out of it) releases the loader, the atlas and the event subscription in reverse order.
Run it twice, as `Run` does, and notice the second call starts from a clean slate.

## Caveat

`Dispose()` is `Terminate()`, so a throwing clean-up surfaces as an `AggregateException` **at the closing
brace** of the `using` block. See sample 02.

This sample is plain C# — the package references neither `UnityEngine` nor `UnityEditor`.
