# 04 — Brackets: acquire/release as one expression

```csharp
lifetime.AddBracket(
    onOpen:      () => Open(),
    onTerminate: () => Close()
);
```

One call, one line, reviewed together — the acquire and the release cannot drift apart across a file.

## Semantics worth knowing

- `onOpen` runs **immediately**, and only then is `onTerminate` registered, so a teardown is never armed
  for a resource that was not acquired. If `onOpen` throws, nothing is registered. *(The order was the
  other way round before 2.0.0.)*
- Brackets close **LIFO**, interleaved with plain `AddAction` registrations in a single sequence.
- `AddBracket` returns the lifetime, so brackets chain.
- On an **already-terminated** lifetime **neither callback runs**. Nothing was acquired, so there is
  nothing to release. This is the deliberate asymmetry with `AddAction` (sample 07) — the shared invariant
  is *every acquired resource is released exactly once*.
- `onOpen` may be `null`: "register `onTerminate`, but only if the scope is still open".

## `With` — the common case

`disposable.With(lifetime)` ties an `IDisposable` to a scope and returns it with its own static type, so
ownership is stated on the same line as construction:

```csharp
var buffer = new Buffer().With(lifetime); // a Buffer, not an IDisposable
```

`Dispose` is called exactly once per `With` call — register an instance on two lifetimes and it is disposed
twice. On a dead lifetime the object **is** disposed immediately (it already exists; dropping the
registration would leak it).

Do not use `With` to tie a `Lifetime.Definition` to another lifetime. It compiles, because a definition is
an `IDisposable`, and the definition does end with the other lifetime — but through a plain action, not as
a nested scope, so nothing detaches it when it ends first: it stays referenced by the other lifetime until
that one ends. Use `other.DefineNested()`, or
`Lifetime.Intersection(...)` when it must end with either of two lifetimes.

## What to look at

- `BracketSample.cs` — ordering, chaining, the dead-scope case, and the throwing-`onOpen` case.
- `DisposableSample.cs` — `With` returning the concrete type, and immediate disposal on a dead scope.

## Run it

```csharp
BracketSample.Run(Console.WriteLine); // or Debug.Log in Unity
DisposableSample.Run(Console.WriteLine);
```

This sample is plain C# — the package references neither `UnityEngine` nor `UnityEditor`.
