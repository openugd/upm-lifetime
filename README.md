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
        Lifetime.Definition definition = Lifetime.Define(Lifetime.Eternal, "example");
        Lifetime lifetime = definition.Lifetime;

        // Run something on termination.
        lifetime.AddAction(() => Console.WriteLine("terminated"));

        // Acquire now, release on termination.
        lifetime.AddBracket(
            onOpen: () => Console.WriteLine("opened"),
            onTerminate: () => Console.WriteLine("closed")
        );

        // Dispose an IDisposable when the lifetime ends.
        var source = new CancellationTokenSource();
        source.With(lifetime);

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
        using (var definition = Lifetime.Define(parent))
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
| `static Lifetime Eternal` | A lifetime that is never terminated. Useful as a root. |
| `static Definition Define(Lifetime lifetime, string id = null)` | Creates a definition nested in `lifetime`. `id` is for debugging only. |
| `static Definition Intersection(params Lifetime[] lifetimes)` | Creates a definition terminated when any of `lifetimes` terminates. |
| `Definition DefineNested(string name = null)` | Instance form of `Define(this, name)`. |
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
| `string Id`, `int ParentId` | Debugging identifiers. |

### `LifetimeExtensions`

| Member | Description |
| --- | --- |
| `IDisposable With(this IDisposable disposable, Lifetime lifetime)` | Disposes `disposable` on termination and returns it. |
| `CancellationToken AsCancellationToken(this Lifetime lifetime)` | A token cancelled when the lifetime terminates. |
| `bool IsAlive(this Lifetime lifetime)` | `!lifetime.IsTerminated`. |
| `void ThrowIfTerminated(this Lifetime lifetime)` | Throws `ObjectDisposedException` if the lifetime has terminated. |

All extension methods throw `ArgumentNullException` for a null lifetime or disposable.

## Samples

The package ships eight small, heavily commented samples. Import them from *Window > Package Manager >
Lifetime > Samples*, or read them straight from `Samples~/` in the repository. Each folder has its own
`README.md`.

| # | Sample | Shows |
| --- | --- | --- |
| 01 | Capability Split | Who holds the `Definition` versus who holds the `Lifetime` — and why that is the whole point. Start here. |
| 02 | Nested Scopes | The parent → child termination cascade, LIFO ordering, and failure isolation on teardown. |
| 03 | Bounded Operation | `using var scope = lifetime.DefineNested(...)`, and the implicit `Definition` → `Lifetime` conversion as a capability downgrade. |
| 04 | Brackets and Disposables | `AddBracket` for acquire/release pairs, and `disposable.With(lifetime)`. |
| 05 | Cancellation | `AsCancellationToken` bridging a scope into `async`/`await` code. |
| 06 | Intersection | A scope that ends when *either* of two independent scopes ends. |
| 07 | Already Terminated | The 2.0.0 rule — registering clean-up on a dead scope runs it immediately — and the class of leaks it removes. |
| 08 | Unity Integration | Binding a scope to a `GameObject`. **The only sample that needs Unity**; 01–07 are plain C#. |

Samples 01–07 are engine-free and take an `Action<string> log`, so they run under any logger:

```csharp
CapabilitySplitSample.Run(Console.WriteLine);  // or Debug.Log in Unity
```

## Requirements

- Unity 2022.3 or newer.
- No other packages required.

## Licence

Apache-2.0 — see [LICENSE.md](LICENSE.md).
