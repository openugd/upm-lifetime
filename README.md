# Lifetime

`com.openugd.lifetime` gives you a scope object, a `Lifetime`, that you register clean-up on. When the scope
ends, the clean-up runs once, at a point you choose and in reverse order, instead of whenever the garbage
collector gets to it. Use it for anything that must be released when its owner goes away: subscriptions,
timers, cancellation tokens, pooled objects, native handles, or a whole screen's worth of state. It is the
same model as JetBrains Rider's `Lifetime`/`LifetimeDefinition`, and it is the base of every other OpenUGD
package.

The package is plain C#. It references neither `UnityEngine` nor `UnityEditor`, so the same code compiles in
an ordinary .NET project.

## Install

### openupm-cli

```bash
openupm add com.openugd.lifetime@2.0.0
```

### Scoped registry

Add the OpenUPM registry and the package to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.openugd"
      ]
    }
  ],
  "dependencies": {
    "com.openugd.lifetime": "2.0.0"
  }
}
```

### Git URL

```json
{
  "dependencies": {
    "com.openugd.lifetime": "https://github.com/openugd/upm-lifetime.git#2.0.0"
  }
}
```

Or use *Window > Package Manager > + > Add package from git URL* and paste
`https://github.com/openugd/upm-lifetime.git#2.0.0`. Lifetime has no dependencies, so this one entry is
enough.

## Requirements

- Unity 6000.0 or newer.
- No other packages.

## Quick start

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using OpenUGD;

// A consumer receives a Lifetime: it can register clean-up, but it cannot end the scope.
public sealed class Connection
{
    public Connection(Lifetime lifetime, string name)
    {
        Console.WriteLine($"{name}: open");
        lifetime.AddAction(() => Console.WriteLine($"{name}: closed"));
    }
}

public static class QuickStart
{
    public static async Task RunAsync()
    {
        // The owner holds the Definition, the only handle that can end the scope.
        Lifetime.Definition session = Lifetime.Eternal.DefineNested("session");
        Lifetime lifetime = session.Lifetime;

        new Connection(lifetime, "connection");

        // Dispose an IDisposable when the scope ends. With returns the type it was given.
        MemoryStream buffer = new MemoryStream().With(lifetime);

        // Acquire now, release when the scope ends.
        lifetime.AddBracket(
            onOpen: () => Console.WriteLine("lock taken"),
            onTerminate: () => Console.WriteLine("lock released"));

        // A child scope for one operation. It ends with its parent, or earlier on its own.
        using (var request = lifetime.DefineNested("request"))
        {
            // The token is cancelled when the request scope ends.
            await Task.Delay(10, request.Lifetime.AsCancellationToken());
        }

        // Ending the session runs its clean-up in reverse registration order:
        // "lock released", then the stream is disposed, then "connection: closed".
        session.Terminate();

        Console.WriteLine(lifetime.IsTerminated); // True
    }
}
```

## Concepts

### Owner and observer

A `Lifetime` can be observed and have clean-up registered on it. Only the `Lifetime.Definition` that owns it
can end it, through `Terminate()` or `Dispose()`. It is the same split as `CancellationTokenSource` and
`CancellationToken`: hand out the `Lifetime`, keep the `Definition`.

```csharp
using OpenUGD;

public sealed class Session
{
    private readonly Lifetime.Definition _definition;

    public Session(Lifetime parent) => _definition = parent.DefineNested("session");

    public Lifetime Lifetime => _definition.Lifetime; // hand this out: observe and register only
    public void Close() => _definition.Terminate();   // keep this: only the owner ends the scope
}
```

A `Definition` converts implicitly to its `Lifetime` wherever C# converts an expression: an argument, an
assignment or a return value. The conversion does not apply to member access, so registering on the scope
itself is still `scope.Lifetime.AddAction(...)`:

```csharp
using var scope = lifetime.DefineNested("load-level");
LoadAtlas(scope);                 // LoadAtlas(Lifetime) - passes the observer, keeps the owner
scope.Lifetime.AddAction(Unload); // member access needs .Lifetime
```

### Scopes form a tree

There are two ways to create a scope:

- `lifetime.DefineNested(name)` creates a child that ends when `lifetime` ends.
- `Lifetime.Intersection(a, b, ...)` creates a scope that ends as soon as any of its inputs ends. Use it for
  something that is valid only while several independent scopes are alive, which one parent cannot express.

Either kind can be ended early by its owner. It then detaches itself from its parents, so a long-lived parent
does not collect dead children. A detach costs amortised O(1), so ending thousands of short scopes one by one
is linear. The root of every tree is `Lifetime.Eternal`, which never ends; see
[Eternal and play sessions](#eternal-and-play-sessions) before you hang things directly off it.

```csharp
var screen = app.DefineNested("screen");
var binding = Lifetime.Intersection(screen.Lifetime, network.Lifetime); // ends with either
```

### Clean-up runs once, in reverse order

- **LIFO.** Termination actions run in reverse registration order: the last thing acquired is released first.
  Child scopes are part of the same sequence, so a child created after an action ends before that action runs.
- **Never dropped.** An action registered on a scope that has already ended runs immediately, on the calling
  thread, before `AddAction` returns. The same rule covers `With` (disposes at once), `AsCancellationToken`
  (returns a cancelled token) and `DefineNested` (returns a definition that is already terminated). So there
  is no need to test `IsTerminated` before registering, and that test would be racy anyway.
- **Failures do not stop the others.** If termination actions throw, every action still runs and the scope
  ends either way. Then one failure is rethrown as itself, with its original stack trace, and two or more are
  thrown together as an `AggregateException`, in the order they occurred. `Dispose()` is `Terminate()`, so
  this can happen at the closing brace of a `using` block.

```csharp
try
{
    definition.Terminate();
}
catch (AggregateException several)
{
    foreach (var failure in several.Flatten().InnerExceptions) Log(failure);
}
catch (Exception one)
{
    Log(one);
}
```

There is no `RemoveAction`. To unregister early, register on a nested definition and terminate it:

```csharp
var subscription = lifetime.DefineNested("hover");
subscription.Lifetime.AddAction(Unsubscribe);
// later, to unregister before lifetime ends:
subscription.Terminate();
```

### Brackets and disposables

`AddBracket(onOpen, onTerminate)` writes acquire and release as one call. `onOpen` runs now, and only then is
`onTerminate` registered, so a release is never armed for something that was not acquired. On a scope that has
already ended, neither runs: nothing was acquired, so there is nothing to release.

`disposable.With(lifetime)` disposes an `IDisposable` when the scope ends and returns it with its own static
type, so ownership is stated where the object is built:

```csharp
var stream = File.OpenRead(path).With(lifetime); // a FileStream, disposed with the scope
```

Do not use `With` to tie a `Lifetime.Definition` to another lifetime. It compiles, because a definition is an
`IDisposable`, but the result is a plain action, not a nested scope: if the definition ends first, nothing
detaches it. Use `other.DefineNested()` or `Lifetime.Intersection(...)` instead.

### Cancellation

`lifetime.AsCancellationToken()` returns a token that is cancelled when the scope ends, so every API that takes
a token becomes scope-aware. On a live scope each call allocates a `CancellationTokenSource` and adds an entry
held until the scope ends: call it once and keep the token, never in a loop against a long-lived scope.

```csharp
await DownloadAsync(url, lifetime.AsCancellationToken());
```

### Threads

Every member is safe to call from any thread. No lock is held while user code runs, so a termination action
may call back into its own scope, its parent or its children. Exactly one caller of `Terminate()` runs the
actions; a concurrent second caller returns at once, possibly before the clean-up has finished.
`IsTerminated` is already `true` while the actions run.

### Eternal and play sessions

`Lifetime.Eternal` is a static field. It never ends and lives as long as the application domain: in a player,
the whole process. In the Unity editor only a domain reload resets it, so with *Enter Play Mode Options* set
to skip the domain reload, `Eternal` and every scope still nested in it, with its clean-up never run, survive
from one play session into the next.

So do not hang services, subscriptions or per-object scopes directly off `Eternal`. Give the application one
root definition nested in `Eternal`, nest everything else in it, and terminate it when the application quits
and, in the editor, when play mode ends:

```csharp
Lifetime.Definition app = Lifetime.Eternal.DefineNested("app"); // once, at startup
// ...everything else: app.Lifetime.DefineNested(...)
app.Terminate();                                                // on quit and when leaving play mode
```

This package has no engine dependency, so it cannot hook Unity's quit and play-mode events itself.
`com.openugd.corelib` 2.0 provides such a root, `OpenUGD.Core.PlaySession.Lifetime`, and a per-GameObject
scope nested in it, `gameObject.GetLifetime()`.

## API overview

All types are in the `OpenUGD` namespace.

### `Lifetime`

| Member | Description |
| --- | --- |
| `static Lifetime Eternal` | A lifetime that never ends: the root of every tree. Process-wide; see [Eternal and play sessions](#eternal-and-play-sessions). |
| `Definition DefineNested(string name = null)` | Creates a scope nested in this lifetime. On a terminated lifetime the new definition is born terminated. `name` is for debugging only. |
| `static Definition Intersection(params Lifetime[] lifetimes)` | Creates a scope that ends when any of `lifetimes` ends. Attached to those lifetimes only. With no arguments, only its owner can end it. Throws `ArgumentNullException` for a null array or element. |
| `Lifetime AddAction(Action action)` | Registers an action to run at termination, LIFO. Duplicates are allowed. On a terminated lifetime the action runs immediately. Throws `ArgumentNullException` for `null`. |
| `Lifetime AddBracket(Action onOpen, Action onTerminate)` | Runs `onOpen` now, then registers `onTerminate`. On a terminated lifetime neither runs. `onOpen` may be `null`; `onTerminate` may not. |
| `bool IsTerminated` | Whether the lifetime has ended. `true` from the moment termination starts. |
| `int Id` | Process-unique instance number, for debugging. |

### `Lifetime.Definition` : `IDisposable`

| Member | Description |
| --- | --- |
| `Lifetime Lifetime` | The lifetime this definition owns. |
| `void Terminate()` | Ends the lifetime and runs its actions in reverse order. Idempotent. Every action runs even if some throw; then one failure is rethrown as itself, two or more as one `AggregateException`. |
| `void Dispose()` | Same as `Terminate()`. |
| `bool IsTerminated` | Whether the owned lifetime has ended. |
| `string Name` | The name given to `DefineNested`, for debugging. May be `null`. |
| `int ParentId` | The `Id` of the lifetime it was defined on, for debugging; `0` for an intersection. |
| `implicit operator Lifetime(Definition)` | Passes a definition where a `Lifetime` is expected. Null-safe. Not applied to member access. |

### `LifetimeExtensions`

| Member | Description |
| --- | --- |
| `T With<T>(this T disposable, Lifetime lifetime) where T : IDisposable` | Disposes `disposable` when the lifetime ends (at once if it has ended) and returns it. |
| `CancellationToken AsCancellationToken(this Lifetime lifetime)` | A token cancelled when the lifetime ends; already cancelled if it has ended. |
| `bool IsAlive(this Lifetime lifetime)` | `!lifetime.IsTerminated`. |
| `void ThrowIfTerminated(this Lifetime lifetime)` | Throws `ObjectDisposedException` if the lifetime has ended. |

Every extension method throws `ArgumentNullException` for a null lifetime or disposable.

## Samples

Import them from *Window > Package Manager > Lifetime > Samples*, or read them in `Samples~/` in the
repository. Each folder has its own `README.md`.

| # | Sample | Shows |
| --- | --- | --- |
| 01 | Capability Split | Who holds the `Definition` and who holds the `Lifetime`, and why that is the point. Start here. |
| 02 | Nested Scopes | The parent-to-child cascade, LIFO order, and how failures are reported on teardown. |
| 03 | Bounded Operation | `using var scope = lifetime.DefineNested(...)`, and the implicit `Definition` to `Lifetime` conversion at call boundaries. |
| 04 | Brackets and Disposables | `AddBracket` for acquire and release, and `var x = new X().With(lifetime)`. |
| 05 | Cancellation | `AsCancellationToken` in `async`/`await` code. |
| 06 | Intersection | A scope that ends when either of two independent scopes ends. |
| 07 | Already Terminated | Registering on a scope that has ended runs the clean-up at once, and the leaks that rule prevents. |
| 08 | Unity Integration | A scope bound to a GameObject, a nested scope for `OnEnable`/`OnDisable`, and async work cancelled by destruction. The only sample that needs Unity; it has an EditMode test in `Tests/`. |

Samples 01 to 07 are plain C# and take an `Action<string>` for output:

```csharp
OpenUGD.Samples.CapabilitySplit.CapabilitySplitSample.Run(Console.WriteLine); // or Debug.Log in Unity
```

## Running the tests

The package ships an EditMode test assembly, `com.openugd.lifetime.tests`. To run it in your project, list the
package under `testables` in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.openugd.lifetime": "2.0.0"
  },
  "testables": [
    "com.openugd.lifetime"
  ]
}
```

Then open *Window > General > Test Runner*, select *EditMode* and run the `com.openugd.lifetime.tests`
assembly. The Unity Test Framework package must be installed; new projects include it.

## Upgrading to 2.0

This section is for users of `com.openugd.lifetime` 1.2.0. Version 2.0.0 requires Unity 6000.0 or newer and
is licensed under Apache-2.0, replacing the modified MIT text that 1.2.0 shipped. Change the version in your
manifest to `2.0.0`, then fix the compile errors below; after that, read the behaviour changes, because
several of them compile unchanged and behave differently.

### Removed and renamed

| 1.2.0 | 2.0 |
| --- | --- |
| `Lifetime.Define(parent, name)` | `parent.DefineNested(name)` |
| `Lifetime.Definition.Define(parent, name)` | `parent.DefineNested(name)` |
| `Lifetime.Definition.Intersection(a, b)` | `Lifetime.Intersection(a, b)` |
| `definition.Id` (the debugging name) | `definition.Name` |
| `IDisposable With(this IDisposable, Lifetime)` | `T With<T>(this T, Lifetime) where T : IDisposable` |

```csharp
// 1.2.0
var scope = Lifetime.Define(parent, "load");
var both = Lifetime.Definition.Intersection(a, b);
Log(scope.Id);
var stream = (FileStream)File.OpenRead(path).With(lifetime);

// 2.0
var scope = parent.DefineNested("load");
var both = Lifetime.Intersection(a, b);
Log(scope.Name);
var stream = File.OpenRead(path).With(lifetime);
```

For a definition, `Lifetime.Define(definition, name)` becomes `definition.Lifetime.DefineNested(name)`. A
null parent now fails with `NullReferenceException` at the call site instead of `ArgumentNullException`.
`With` changed its signature, so assemblies compiled against 1.2.0 must be recompiled.

### Registering on a scope that has ended

In 1.2.0 an action registered on a terminated lifetime was dropped without a word, so a disposable was never
disposed and a token from `AsCancellationToken` never cancelled. In 2.0 the action runs immediately, on the
calling thread, before `AddAction` returns, and an exception it throws reaches you. `With` disposes at once,
and `AsCancellationToken` returns a cancelled token.

```csharp
// 1.2.0: a guard was needed, and it was racy
if (!lifetime.IsTerminated) lifetime.AddAction(Release);

// 2.0: Release runs at termination, or now if the lifetime has already ended
lifetime.AddAction(Release);
```

**Affects you if** you register clean-up on a scope that may already have ended: that clean-up now runs.

`DefineNested` on a terminated lifetime no longer throws `InvalidOperationException`; it returns a definition
that is already terminated, and anything registered on it runs at once.

```csharp
// 1.2.0
try { child = parent.DefineNested(); }
catch (InvalidOperationException) { return; }

// 2.0
var child = parent.DefineNested();
if (child.IsTerminated) return;
```

### Failures during termination

In 1.2.0 the first throwing action stopped the rest. In 2.0 every action runs, and only then are the failures
reported: one failure as itself, two or more as one `AggregateException`. A nested scope's failure reaches the
parent as whatever the nested scope threw. A second `Terminate()` neither re-runs nor re-throws. **Affects you
if** your clean-up can throw: keep catching the exception types it throws, and add `AggregateException` where
several actions can fail together.

### Registration rules

- **Duplicates are allowed.** `AddAction` with a delegate that is already registered registers it again and
  runs it once per registration. 1.2.0 threw `ArgumentException("Action already exist")`.
- **Null is rejected.** `AddAction(null)` and `AddBracket(onOpen, null)` throw `ArgumentNullException`. 1.2.0
  stored the null and skipped it. `AddBracket(null, onTerminate)` is still allowed.
- **Brackets open first.** `AddBracket` runs `onOpen` before it registers `onTerminate`, so if `onOpen` throws,
  `onTerminate` never runs. 1.2.0 registered the release first. Neither callback runs while a lock is held.
- **Intersection validates its arguments.** A null array or element throws `ArgumentNullException` before
  anything is wired up. In 1.2.0 a null element threw `NullReferenceException` halfway through and left a
  half-attached definition behind.
- **Intersection is not attached to `Eternal`.** An intersection that is never terminated is collected with
  its inputs instead of living for the rest of the process. Its `ParentId` is `0`.

### Threads

- `Terminate` no longer holds a lock while it runs your actions, so actions may re-enter the lifetime tree
  freely, and actions that relied on that lock for mutual exclusion no longer get it.
- A second thread that calls `Terminate` while the first is still running the actions returns at once instead
  of waiting. If you need to know that clean-up has finished, terminate from the thread that owns the
  definition.
- `IsTerminated` is a lock-free read, and it is `true` while the actions are still running.

### What stays the same

- Only the holder of a `Definition` can end a lifetime; `Lifetime.Terminate()` is still private.
- Actions run in reverse registration order, now a documented guarantee.
- `Lifetime.Id`, `Definition.ParentId` and `Definition.Name` are debugging aids with no semantics.

## Versioning

The OpenUGD packages share a major version: every package of the family is 2.x. Minor and patch versions move
independently. Each 2.x package works with the 2.x versions of its dependencies at or above the minimums
declared in its `package.json`. Lifetime has no dependencies; packages that depend on it declare the minimum
Lifetime version they need, and UPM installs the highest version that any package in the project asks for.

## Licence

Apache-2.0. See [LICENSE.md](LICENSE.md).
