# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0]
All packages are reset to a synchronized major version. This release contains behavioural breaks:
read Changed and Removed before upgrading.

### Added
- `DisposableHandler` — collects `IDisposable`s and disposes them when a `Lifetime` ends, moved here from
  `com.openugd.corelib`. Pure lifetime plumbing with no Unity dependency, so it belongs beside `Lifetime`.
- `implicit operator Lifetime(Lifetime.Definition)`, so scope code can pass a definition anywhere a
  lifetime is expected without writing `.Lifetime`. Null-safe: a null definition converts to a null
  lifetime. Additive, but it can change overload resolution at a call site that has both a
  `Definition` overload and a `Lifetime` overload.
- Full XML documentation on every public member, including explicit warnings at each call site whose
  behaviour changed.

### Changed
- **`AddAction` on an already-terminated lifetime now invokes the action immediately** instead of
  silently dropping it. Affects you if you ever register clean-up on a lifetime that may already have
  ended — that clean-up now actually runs, on the calling thread, before `AddAction` returns, and an
  exception it throws now propagates to you. This is the fix for a whole family of downstream bugs:
  `With()` never disposing, `AsCancellationToken()` handing out a token that never cancels,
  `Signal.Subscribe()` returning `true` having subscribed nothing, and `KeepReference` leaking a count.
- **`AddAction` no longer throws `ArgumentException("Action already exist")` for a duplicate delegate.**
  Duplicates are allowed and run once per registration, matching ordinary .NET multicast semantics.
  Affects you if you relied on the throw as a guard; it also removes an O(n) scan per registration.
- **`AddAction(null)` now throws `ArgumentNullException`.** Previously the null was stored and skipped
  at invoke time. Affects you only if you were passing null by accident.
- **`AddBracket` now invokes `onOpen` before registering `onTerminate`** (1.x registered the teardown
  first). Consequence: if `onOpen` throws, `onTerminate` is no longer registered and will not run —
  nothing was acquired, so there is nothing to release. Affects you if you depended on teardown
  running after a failed acquisition.
- **`AddBracket(onOpen, null)` now throws `ArgumentNullException`**, validated before `onOpen` runs so
  a resource is never acquired by a call that is about to fail. `AddBracket(null, onTerminate)` is
  still legal and means "register this, but only if the scope is still open".
- **`AddBracket` on an already-terminated lifetime runs neither callback.** Unchanged in outcome, but
  it is now a documented guarantee with a rationale rather than an accident of the dropped
  registration, and `onOpen` no longer runs while an internal lock is held.
- **A throwing termination action no longer aborts the remaining actions.** Every registered action
  now runs, and the collected failures are rethrown as a single `AggregateException` at the end.
  Affects you if you call `Terminate()` or `Dispose()`: **both can now throw**, including at the
  closing brace of a `using` block, where it can mask an in-flight exception from the block body.
  Termination is still complete and idempotent — a second `Terminate()` neither re-runs nor re-throws.
  Nested scopes produce nested aggregates; call `AggregateException.Flatten()` for the leaves.
- **`Terminate` no longer holds the instance lock while invoking callbacks**, and `AddDefinition` no
  longer calls into another lifetime under its own lock. This removes a parent/child ABBA lock
  inversion that could deadlock. Affects you two ways: termination actions may now freely re-enter the
  lifetime tree (registering during termination invokes immediately), and actions that were
  implicitly relying on the lock for mutual exclusion no longer get it.
- **`Terminate` called concurrently from a second thread returns immediately** rather than waiting for
  the in-progress teardown. Affects you if you race two threads to terminate one scope and need
  "clean-up has finished" — own the definition instead.
- **`IsTerminated` is `true` while the termination actions are still running**, and is now a lock-free
  read. Previously a concurrent reader blocked until the drain finished.
- **`AsCancellationToken` on an already-terminated lifetime now returns an already-cancelled token**
  (previously a token that could never cancel, so any `await` on it hung forever). It allocates no
  `CancellationTokenSource` in that case. The source is documented as intentionally never disposed,
  so callers still holding the token are never broken.
- **`With(IDisposable, Lifetime)` on an already-terminated lifetime now disposes immediately**
  (previously the object was never disposed at all). An exception from that `Dispose` propagates.
- **`Intersection` now throws `ArgumentNullException`** for a null array or a null element, validated
  before anything is wired up. Previously a `NullReferenceException` part-way through wiring left a
  half-attached definition on `Lifetime.Eternal` forever.


### Changed (packaging)
- **Licence changed from MIT to Apache-2.0.** The previous `LICENSE` was a mutated MIT whose copyright
  line had been deleted and whose attribution clause was replaced with the literal text "No conditions.",
  which left it legally ambiguous. It is now the verbatim Apache License 2.0 with an explicit copyright
  holder, the file is named `LICENSE.md`, and `package.json` declares `"license": "Apache-2.0"`.
  Apache-2.0 adds an express patent grant and requires that changes to the files be stated; releases made
  before this version remain under their original terms.
- Minimum supported Unity version raised to 2022.3 (`unityRelease` `0f1`). The previously declared
  minimums were never verified against a build.
- Package manifest updated to the current Unity package schema: object `author`, a real `description`,
  `licensesUrl`/`documentationUrl`/`changelogUrl` and a `repository` object; the obsolete `category`
  key was removed.
- Assembly definition now declares `rootNamespace` `OpenUGD`, `noEngineReferences: true` and the full
  canonical key set instead of relying on editor defaults.
- README rewritten with install instructions, a quick start and an API overview.
- The package now ships its own test assembly (`Tests/Editor`, 85 tests, gated on `UNITY_INCLUDE_TESTS`).

### Removed
- **The explicit `void IDisposable.Dispose()` on `Definition`.** The public `Dispose()` already
  satisfies the interface, and keeping it public means `using` does not box. Source-compatible;
  **binary-breaking** for precompiled assemblies bound to the explicit interface slot — recompile.
- **The static `Stack<List<Action>>` pool.** It was popped under `lock(_pool)` in the constructor and
  pushed under the instance lock in `Terminate` — unsynchronized mutation of a shared `Stack` that
  could hand the same `List<Action>` to two live lifetimes. Each lifetime now allocates its own list,
  lazily. No API change.
- **Deduplication in `AddDefinition`.** Attaching the same definition to the same parent twice now
  registers twice, which is harmless: `Terminate` is idempotent and each attach registers its own
  detach. Removes another O(n) scan.

### Unchanged (explicitly)
- `Lifetime.Terminate()` remains private: only the holder of a `Definition` can end a lifetime.
- Termination actions run in **reverse registration order (LIFO)** — now a documented guarantee.
- There is still **no `RemoveAction`**. To unregister, create a nested definition, register on it, and
  terminate it; that also detaches it from its parent.
- `Define` on a terminated lifetime still throws `InvalidOperationException`, with the same message.
- `Id` / `ParentId` remain debugging aids with no semantics.

## [1.2.0]
### Changed
- LifetimeExtensions.cs

## [1.1.0]
### Added
- LifetimeExtensions.cs

## [1.0.0]
### Added
- Lifetime.cs
