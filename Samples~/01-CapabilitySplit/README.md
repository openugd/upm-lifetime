# 01 — Capability split: Definition vs Lifetime

The idea the whole package is built on.

`Lifetime.Terminate()` is **private**. The only public way to end a scope is
`Lifetime.Definition.Terminate()` / `.Dispose()`, and you only get a `Definition` by creating one. So:

| You hold | You can |
| --- | --- |
| `Lifetime.Definition` | end the scope, and everything below |
| `Lifetime` | observe (`IsTerminated`, `IsAlive()`), register clean-up (`AddAction`, `AddBracket`, `With`) |

Same split as `CancellationTokenSource` vs `CancellationToken`. Handing out a `Lifetime` hands out
**no authority to kill it**, so you can pass one to any number of subsystems without auditing them.

## What to look at

`CapabilitySplitSample.cs`:

- `Session` — the **owner**. Its `Lifetime.Definition` is a private field; the public surface is a
  `Lifetime` property. This is the pattern to copy.
- `Connection` — a **consumer**. It receives a `Lifetime`, registers its own teardown on it, and guards
  its work with `IsAlive()`.
- The commented-out `lifetime.Terminate();` inside `Connection`'s constructor: it does not compile. That
  non-compiling line is the sample.

## Run it

```csharp
CapabilitySplitSample.Run(Console.WriteLine); // or Debug.Log in Unity
```

Expected order — note the LIFO teardown, `Connection: closed` before `Session: closed`:

```
-- creating the owner (it holds the Definition) --
Session: opened (lifetime #N, parent #1)
Connection: opened
Connection: sent 'hello'
-- owner ends the scope --
Connection: closed
Session: closed
session alive: False
Connection: dropped 'too late' (scope has ended)
```

This sample is plain C# — the package references neither `UnityEngine` nor `UnityEditor`.
