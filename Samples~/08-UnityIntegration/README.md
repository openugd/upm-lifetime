# 08 — Unity integration

**The package itself has no engine dependency.** `Runtime` is compiled with `noEngineReferences` and
references neither `UnityEngine` nor `UnityEditor`, so the same code compiles in a plain .NET project.
Samples 01–07 are plain C#. This folder is the only one that needs Unity, and it is deliberately kept
separate so that the split is visible.

The adapter is one small component. There is nothing Unity-specific inside the library, and there does not
need to be.

## How to use it

Attach `LifetimeScope` to a GameObject, then add `ScopedConsumer` and/or `ScopedAsyncWork` to the same
object and press Play. Watch the Console, then disable the component, then destroy the object.

No scene asset is shipped — three components on one empty GameObject is the entire setup, and building it
by hand takes ten seconds.

`Tests/` holds EditMode checks of the adapter. They compile only in a project with the Unity Test
Framework; delete the folder if you do not want them in your Test Runner.

## What to look at

### `LifetimeScope.cs`

Binds a scope to a GameObject: opens when the object is first activated and ends in `OnDestroy`. It is the
**owner** — the `Lifetime.Definition` is a private field and the public surface is a `Lifetime` property,
so other components can register clean-up but cannot destroy the scope.

The scope is created in `Awake`, or earlier in the same activation when another component's `Awake` or
`OnEnable` reads `Lifetime` first. Reading `Lifetime` on an object that has never been active throws — see
the caveats below for why.

`Terminate()` rethrows a clean-up action's exception out of `OnDestroy` (an `AggregateException` if several
throw). That is fine: Unity logs it, and every other clean-up has already run by then.

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

Call `AsCancellationToken` once per component, never in `Update` or a loop: each call on a live lifetime
allocates a source and adds an entry held until termination.

An `async` method started on the main thread resumes on the main thread: Unity installs a
`UnitySynchronizationContext` there, the `await` captures it, and the code after `await Task.Delay(...)` runs
on a later frame, on the main thread, so it may touch Unity objects. Code after `ConfigureAwait(false)`, or
started with `Task.Run`, resumes on a thread-pool thread instead. Resuming on the main thread is not the same
as resuming only while the object exists — that is what the token is for.

## Caveats

- **Objects that are never activated.** Unity calls `OnDestroy` only on GameObjects that have been active.
  A scope created for an object that never is — say, by a lazy getter on an inactive prefab instance —
  would never be terminated, and would stay nested in `Lifetime.Eternal` for the rest of the process with
  everything registered on it. That is why `LifetimeScope` creates its scope during activation and throws
  when `Lifetime` is read on an object that has never been active. Activate the object first, or read the
  scope from `Start` or later. Edit Mode is the same story: Unity calls neither `Awake` nor `OnDestroy` on
  these components there, so do not read `Lifetime` from editor code.
- **Domain reload turned off.** `Lifetime.Eternal` is a static field. With *Enter Play Mode Options* set to
  skip the domain reload, it survives from one play session to the next, together with every scope still
  nested in it. This sample stays clean only because every scope it creates ends in `OnDestroy`, which Unity
  runs for every object it destroys when Play Mode ends. Anything registered directly on `Eternal`, or on a
  scope that no `OnDestroy` ends, carries over into the next session. In a project, nest these scopes in a
  play-session root that you terminate when Play Mode ends — see "Eternal and play sessions" in the package
  README.
- **Scopes do not nest with the transform hierarchy.** Every `LifetimeScope` is nested directly in
  `Lifetime.Eternal`, so a child object's scope is a sibling of its parent's. Destroying a parent object
  still ends both, because Unity destroys the children with it and each runs its own `OnDestroy` — but in
  Unity's order, not as one LIFO cascade. To nest for real, look up the parent's scope in `Awake`
  (`transform.parent.GetComponentInParent<LifetimeScope>()`) and `DefineNested` on it. The price: the scope
  stays with the parent it had in `Awake`, so after re-parenting, destroying the old parent ends the scope
  of an object that is still alive.
