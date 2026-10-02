# Lifetime

Structured object lifetimes for C# and Unity: a composable scope primitive for deterministic cleanup.

A `Lifetime` is a scope you can attach termination actions to. You never construct one directly — you create a
`Lifetime.Definition`, which owns the lifetime and is the only thing that can terminate it. When the definition is
terminated (or disposed), every registered action runs in reverse registration order and nested definitions are
terminated with it. That gives you a single explicit point at which subscriptions, timers, tokens and disposables
are released, instead of relying on the garbage collector. It is the same model as JetBrains Rider's
`Lifetime`/`LifetimeDefinition`.

The package is pure C# — it references neither `UnityEngine` nor `UnityEditor` — so the same code compiles in a
plain .NET project.

## Install

### openupm-cli

```bash
openupm add com.openugd.lifetime
```

### Scoped registry

Add to `Packages/manifest.json`:

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

In `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.openugd.lifetime": "https://github.com/openugd/upm-lifetime.git"
  }
}
```

Or use *Window > Package Manager > + > Add package from git URL* and paste
`https://github.com/openugd/upm-lifetime.git`.

## Quick start

```csharp
using System;
using System.Threading;
using OpenUGD;

public class Example
{
    public void Run()
    {
        // A definition owns a lifetime; only the owner can terminate it.
        Lifetime.Definition definition = Lifetime.Eternal.DefineNested("example");
        Lifetime lifetime = definition.Lifetime;

        // Run something on termination.
        lifetime.AddAction(() => Console.WriteLine("terminated"));

        // Acquire now, release on termination.
        lifetime.AddBracket(
            onOpen: () => Console.WriteLine("opened"),
            onTerminate: () => Console.WriteLine("closed")
        );

        // Dispose an IDisposable when the lifetime ends. With returns the type it was given.
        CancellationTokenSource source = new CancellationTokenSource().With(lifetime);

        // Bridge to async code.
        CancellationToken token = lifetime.AsCancellationToken();

        // Nested scopes die with their parent, and can also be terminated earlier on their own.
        Lifetime.Definition child = lifetime.DefineNested("child");
        child.Lifetime.AddAction(() => Console.WriteLine("child terminated"));
        child.Terminate();

        Console.WriteLine(lifetime.IsAlive()); // True
        lifetime.ThrowIfTerminated();          // no-op while alive

        // Terminating the definition runs the registered actions in reverse order.
        definition.Terminate();

        Console.WriteLine(lifetime.IsTerminated); // True
    }

    // A definition is IDisposable, so `using` works too.
    public void Scoped(Lifetime parent)
    {
        using (var definition = parent.DefineNested("scoped"))
        {
            definition.Lifetime.AddAction(() => Console.WriteLine("scope closed"));
        }
    }

    // A definition that dies as soon as any of the given lifetimes dies.
    public Lifetime.Definition Both(Lifetime a, Lifetime b)
    {
        return Lifetime.Intersection(a, b);
    }
}
```

## API overview

### `Lifetime`

| Member | Description |
| --- | --- |
| `static Lifetime Eternal` | A lifetime that is never terminated: the root of every tree. Process-wide — see [Eternal and play sessions](#eternal-and-play-sessions). |
| `Definition DefineNested(string name = null)` | Creates a scope nested in this lifetime. If this lifetime has already terminated, the new definition is **born terminated**. `name` is for debugging only. |
| `static Definition Intersection(params Lifetime[] lifetimes)` | Creates a scope terminated when any of `lifetimes` terminates. |
| `Lifetime AddAction(Action action)` | Registers an action to run on termination, LIFO. Duplicates are allowed. If the lifetime has **already terminated the action is invoked immediately**, never dropped. |
| `Lifetime AddBracket(Action onOpen, Action onTerminate)` | Invokes `onOpen` now, then registers `onTerminate` for termination. If the lifetime has already terminated, **neither** runs — nothing was acquired, so there is nothing to release. |
| `bool IsTerminated` | Whether the lifetime has been terminated. |
| `int Id` | Instance id, for debugging. |

### `Lifetime.Definition` (`IDisposable`)

| Member | Description |
| --- | --- |
| `Lifetime Lifetime` | The lifetime this definition owns. |
| `void Terminate()` | Terminates the lifetime and runs its actions in reverse order. Idempotent. |
| `void Dispose()` | Same as `Terminate()`. |
| `bool IsTerminated` | Whether the owned lifetime has been terminated. |
| `string Name`, `int ParentId` | Debugging identifiers: the name given to `DefineNested`, and the `Id` of the parent lifetime. |
| `implicit operator Lifetime(Definition)` | Lets a definition be passed where a `Lifetime` is expected. Not applied to member access: write `scope.Lifetime.AddAction(...)`. |

### `LifetimeExtensions`

| Member | Description |
| --- | --- |
| `T With<T>(this T disposable, Lifetime lifetime) where T : IDisposable` | Disposes `disposable` on termination and returns it with its own type. Not for tying a `Definition` to another lifetime — use `DefineNested` or `Intersection`. |
| `CancellationToken AsCancellationToken(this Lifetime lifetime)` | A token cancelled when the lifetime terminates. |
| `bool IsAlive(this Lifetime lifetime)` | `!lifetime.IsTerminated`. |
| `void ThrowIfTerminated(this Lifetime lifetime)` | Throws `ObjectDisposedException` if the lifetime has terminated. |

All extension methods throw `ArgumentNullException` for a null lifetime or disposable.

### One way to create a scope

`lifetime.DefineNested(name)` creates a child that ends with `lifetime`; `Lifetime.Intersection(a, b, ...)`
creates a scope that ends with whichever of its inputs ends first. Both can be ended early by their owner,
and then detach themselves, so a long-lived parent does not accumulate dead children. There is no third
way: `With` on a `Definition` compiles (a definition is an `IDisposable`) but neither nests it nor detaches
it.

## Eternal and play sessions

`Lifetime.Eternal` is a static field. It is never terminated and lives as long as the application domain: in
a player, the whole process. In the Unity editor only a domain reload resets it, so with *Enter Play Mode
Options* set to skip the domain reload, `Eternal` — and every scope still nested in it, with its registered
clean-up never run — survives from one play session into the next.

So do not hang services, subscriptions or per-object scopes directly off `Eternal`. Give the application
one root definition nested in `Eternal`, nest everything else in that root, and terminate the root when the
application quits and, in the editor, when play mode ends:

```csharp
Lifetime.Definition app = Lifetime.Eternal.DefineNested("app"); // once, at startup
// ...everything else: app.Lifetime.DefineNested(...)
app.Terminate();                                                // on quit / leaving play mode
```

This package has no engine dependency, so it cannot hook Unity's quit and play-mode events itself; in Unity
that means `Application.quitting` and `EditorApplication.playModeStateChanged`. `com.openugd.corelib` is
planned to provide such a play-session root.

## Samples

The package ships eight small, heavily commented samples. Import them from *Window > Package Manager >
Lifetime > Samples*, or read them straight from `Samples~/` in the repository. Each folder has its own
`README.md`.

| # | Sample | Shows |
| --- | --- | --- |
| 01 | Capability Split | Who holds the `Definition` versus who holds the `Lifetime` — and why that is the whole point. Start here. |
| 02 | Nested Scopes | The parent → child termination cascade, LIFO ordering, and failure isolation on teardown. |
| 03 | Bounded Operation | `using var scope = lifetime.DefineNested(...)`, and the implicit `Definition` → `Lifetime` conversion as a capability downgrade (and where it does not apply). |
| 04 | Brackets and Disposables | `AddBracket` for acquire/release pairs, and `var x = new X().With(lifetime)`. |
| 05 | Cancellation | `AsCancellationToken` bridging a scope into `async`/`await` code. |
| 06 | Intersection | A scope that ends when *either* of two independent scopes ends. |
| 07 | Already Terminated | The 2.0.0 rule — registering clean-up on a dead scope runs it immediately — and the class of leaks it removes. |
| 08 | Unity Integration | Binding a scope to a `GameObject`. **The only sample that needs Unity**; 01–07 are plain C#. |

Samples 01–07 are engine-free and take an `Action<string> log`, so they run under any logger:

```csharp
CapabilitySplitSample.Run(Console.WriteLine);  // or Debug.Log in Unity
```

## Requirements

- Unity 6000.0 or newer.
- No other packages required.

## Licence

Apache-2.0 — see [LICENSE.md](LICENSE.md).
