# 08 — Unity integration

**The package itself has no engine dependency.** `Runtime` is compiled with `noEngineReferences` and
references neither `UnityEngine` nor `UnityEditor`, so the same code compiles in a plain .NET project.
Samples 01–07 are plain C#. This folder is the only one that needs Unity, and it is deliberately kept
separate so that the split is visible.

The adapter is about thirty lines. There is nothing Unity-specific inside the library, and there does not
need to be.

## How to use it

Attach `LifetimeScope` to a GameObject, then add `ScopedConsumer` and/or `ScopedAsyncWork` to the same
object and press Play. Watch the Console, then disable the component, then destroy the object.

No scene asset is shipped — three components on one empty GameObject is the entire setup, and building it
by hand takes ten seconds.

## What to look at

### `LifetimeScope.cs`

Binds a scope to a GameObject: opens in `Awake` (lazily, so another component's `Awake` may reach for it
first) and ends in `OnDestroy`. It is the **owner** — the `Lifetime.Definition` is a private field and the
public surface is a `Lifetime` property, so other components can register clean-up but cannot destroy the
scope. Nest these on nested GameObjects and the scopes nest with them, because `OnDestroy` already
cascades down the transform hierarchy.

`Terminate()` may throw an `AggregateException` out of `OnDestroy` if a clean-up action throws. That is
fine: Unity logs it, and every other clean-up has already run by then.

### `ScopedConsumer.cs`

The two scopes you usually want: *while this object exists* (the `LifetimeScope`) and *while this component
is enabled* (a nested definition created in `OnEnable`, terminated in `OnDisable`). Because the enabled
scope is nested, `OnDisable`/`OnDestroy` ordering stops being something you reason about — whichever runs
first, the clean-up runs exactly once.

### `ScopedAsyncWork.cs`

`AsCancellationToken` makes destruction cancel in-flight async work, so an `async` method can no longer
resume into a destroyed component. It also shows the discipline the token is worthless without: **do not
write `_ = DoWorkAsync();`** — a discarded task swallows every error inside it. Keep the task and observe
it.

Two practical notes: call `AsCancellationToken` once per component, never in `Update` or a loop (each call
on a live lifetime allocates a source and adds an entry held until termination); and `Task.Delay` resumes
on a thread pool thread, so touching `UnityEngine` objects after an `await` still needs the usual care —
a lifetime solves ownership, not thread affinity.
