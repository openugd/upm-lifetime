# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-10-03

Version 2.0.0, the first of the synchronized OpenUGD 2.x family. It contains breaking changes; the README's
"Upgrading to 2.0" section walks through each one with before and after code.

### Added
- `implicit operator Lifetime(Lifetime.Definition)`: pass a definition where a `Lifetime` is expected.
  Null-safe; not applied to member access. Affects you if a definition is passed to overloads of `Lifetime`
  and `object` (now binds to `Lifetime`) or of `Lifetime` and `IDisposable` (now ambiguous).
- XML documentation on every public member.
- Eight samples in `Samples~`, one per concept; sample 08 (Unity Integration) has an EditMode test.
- An EditMode test assembly, `com.openugd.lifetime.tests`, gated on `UNITY_INCLUDE_TESTS`.

### Changed
- **Registering on a terminated lifetime runs at once.** `AddAction` invokes the action before returning,
  `With` disposes, `AsCancellationToken` returns a cancelled token. Affects you if you register clean-up on a
  scope that may have ended: it used to be dropped silently.
- **`DefineNested` on a terminated lifetime returns a terminated definition** instead of throwing
  `InvalidOperationException`.
- **`With` is generic**, `T With<T>(this T, Lifetime) where T : IDisposable`, and returns the type it was
  given. Binary-breaking: recompile.
- **`Definition.Id` is renamed `Name`**; parameters that take it are called `name`.
- **A throwing termination action no longer stops the others.** All run; then one failure is rethrown as
  itself, two or more as one `AggregateException`. Affects you if clean-up can throw.
- **`AddAction` accepts duplicate delegates** and runs each registration; it no longer throws
  `ArgumentException`.
- **`AddAction(null)` and `AddBracket(onOpen, null)` throw `ArgumentNullException`** instead of storing the
  null.
- **`AddBracket` runs `onOpen` before registering `onTerminate`**, so a throwing `onOpen` registers nothing. On
  a terminated lifetime neither callback runs, and neither runs under a lock.
- **`Intersection` throws `ArgumentNullException`** for a null array or element, before wiring anything.
- **`Terminate` holds no lock while it runs actions**, which also removes a parent/child deadlock; actions may
  re-enter the lifetime tree. Affects you if an action relied on that lock for mutual exclusion.
- **A concurrent second `Terminate` returns at once** instead of waiting for the first to finish.
- **`IsTerminated` is lock-free** and is `true` while the actions are still running.
- Minimum Unity version raised to 6000.0.
- Licence changed from a modified MIT text to Apache-2.0. Earlier releases keep their original terms.
- `package.json` follows the current Unity schema; the asmdef declares `rootNamespace` and
  `noEngineReferences`.

### Removed
- **`Lifetime.Define`, `Lifetime.Definition.Define` and `Lifetime.Definition.Intersection`.** Use
  `parent.DefineNested(name)` and `Lifetime.Intersection(...)`.
- The explicit `IDisposable.Dispose` on `Definition`; the public `Dispose()` implements the interface.
  Source- and binary-compatible.

### Fixed
- `Intersection` no longer attaches its definition to `Lifetime.Eternal`, which kept every intersection never
  terminated, and the lifetimes it intersected, alive for the process. Its `ParentId` is now `0`.
- A data race on the shared action-list pool, which could hand one list to two live lifetimes. The pool is
  gone.
- Detaching a nested definition scanned the parent's whole list, so ending many scopes was quadratic. It is
  now amortised O(1). No API change.

## [1.2.0]
### Added
- `IsAlive()` and `ThrowIfTerminated()` extension methods.
- XML documentation comments.

### Changed
- `With` returns the `IDisposable` it was given; the extension methods throw `ArgumentNullException` for a
  null argument.

## [1.1.0]
### Added
- `AsCancellationToken()` extension method.

## [1.0.0]
### Added
- `Lifetime`, `Lifetime.Definition` and the `With` extension method.
