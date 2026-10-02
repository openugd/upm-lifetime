# 05 — `AsCancellationToken`: bridging a scope into async code

```csharp
CancellationToken token = lifetime.AsCancellationToken();
await SomeApiAsync(token);
```

A scope and a `CancellationToken` are the same idea seen from two sides, so the conversion is one method.
Everything that already takes a token — `Task.Delay`, `HttpClient`, your own `async` methods — becomes
scope-aware for free, and your async code keeps the .NET-idiomatic signature instead of taking a
`Lifetime` parameter.

## What to look at

`CancellationSample.cs`, four cases:

1. **Terminate cancels in-flight work.** `DownloadAsync` never mentions `Lifetime`; it only sees a token.
2. **Work that completes first.** Closing the scope afterwards is harmless.
3. **A token from an already-terminated lifetime is born cancelled.** Before 2.0.0 it was a token that
   could never cancel, so an `await` on it hung forever. Now it fails fast.
4. **Per-operation tokens.** Each call on a live lifetime allocates one `CancellationTokenSource` and adds
   one entry to that lifetime's action list, held until termination. So call it once and keep the token,
   or scope it to a nested definition — never in a loop against a long-lived lifetime, and never against
   `Lifetime.Eternal`, which never terminates.

## Ownership

The `CancellationTokenSource` is deliberately never disposed: the token is handed out and the method
cannot know who still holds it, and after `Dispose` both `token.Register(...)` and `token.WaitHandle`
throw. It is not a leak — a `CancellationTokenSource` has no finaliser and holds no unmanaged resource
unless someone materialises `WaitHandle` or sets a timer.

## Run it

```csharp
await CancellationSample.RunAsync(Console.WriteLine);
```

This sample is plain C# (`System.Threading.Tasks` only) — the package references neither `UnityEngine` nor
`UnityEditor`.
