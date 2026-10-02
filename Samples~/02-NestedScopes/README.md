# 02 — Nested scopes and the termination cascade

Scopes form a tree. `parent.DefineNested("child")` wires the child's termination into the parent, so:

- terminating the **parent** terminates every child, recursively;
- terminating a **child** first unregisters it from the parent, so a long-lived parent with many
  short-lived children does not accumulate dead entries;
- a child never kills its parent or its siblings;
- `DefineNested` on an already-terminated lifetime **throws** `InvalidOperationException` — you can never
  end up with a live child of a dead parent.

## What to look at

`NestedScopesSample.cs` — builds `app > session > (request-A, request-B)`, ends `request-A` early, then
ends `app` and shows the cascade reaching two levels down.

`TerminationOrderSample.cs` — the two guarantees you can build on:

- **LIFO**: termination actions run in reverse registration order. Last acquired, first released.
- **Failure isolation** (new in 2.0.0): a throwing termination action no longer aborts the remaining ones.
  Everything runs, the lifetime terminates, and the failures surface together as one `AggregateException`.
  Since `Dispose()` is `Terminate()`, that exception can be thrown at the closing brace of a `using` block.

## Run it

```csharp
NestedScopesSample.Run(Console.WriteLine);
TerminationOrderSample.Run(Console.WriteLine);
```

This sample is plain C# — the package references neither `UnityEngine` nor `UnityEditor`.
