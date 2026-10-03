# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0]
All packages are reset to a synchronized major version. This release contains behavioural breaks:
read Changed and Removed before upgrading.

### Added
- `implicit operator Lifetime(Lifetime.Definition)`, so a definition can be passed as an argument, assigned
  or returned where a lifetime is expected without writing `.Lifetime`. Null-safe: a null definition
  converts to a null lifetime. C# never applies it to the receiver of a member or extension-method call,
  so `scope.Lifetime.AddAction(...)` still needs `.Lifetime`. Additive, but it can change overload
  resolution at a call site that has both a `Definition` overload and a `Lifetime` overload.
- Full XML documentation on every public member, including explicit warnings at each call site whose
  behaviour changed.

### Changed
- **`DefineNested` on an already-terminated lifetime returns a definition that is already terminated**
  instead of throwing `InvalidOperationException`, the same rule as `AddAction`, `With` and
  `AsCancellationToken` on a dead scope; anything registered on the returned definition runs immediately.
  A live child of a dead parent is still never produced. Migration: delete the `try`/`catch`; test
  `IsTerminated` on the result if you need to know.
- **`With` is generic: `T With<T>(this T disposable, Lifetime lifetime) where T : IDisposable`**, and
  returns the static type it was given instead of `IDisposable`, so `var stream =
  File.OpenRead(path).With(lifetime);` is a `FileStream`. Migration: delete casts such as
  `(MyType)x.With(lifetime)`; `IDisposable d = x.With(lifetime);` still compiles. Binary-breaking —
  recompile.
- **`Lifetime.Definition.Id` is renamed `Name`.** It is the optional debugging name (a `string`) and no
  longer shares a member name with `Lifetime.Id` (an `int`). Every parameter that takes it is called
  `name`. Migration: `definition.Id` → `definition.Name`.
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
  now runs, and only then are the failures reported: **exactly one failure is rethrown as itself**,
  with its original type and stack trace (via `ExceptionDispatchInfo`), as 1.x let it escape; **two or
  more are thrown together as one `AggregateException`**, in the order they occurred (reverse
  registration order). A nested scope's failure reaches its parent as whatever the nested scope threw,
  so a single failure anywhere in a tree arrives unwrapped and aggregates nest only where one scope
  collected several; call `AggregateException.Flatten()` for the leaves. `Terminate()` and `Dispose()`
  can throw at the closing brace of a `using` block, where the exception masks an in-flight one from
  the block body. Termination is still complete and idempotent — a second `Terminate()` neither re-runs
  nor re-throws. Migration: keep catching the exception types your clean-up throws, and add
  `AggregateException` where several clean-ups can fail together.
- **Detaching a nested definition is amortised O(1)**, so terminating n scopes one by one is linear in n.
  A child that ends first used to be removed from its parent with `List.Remove` and delegate equality,
  an O(n) scan or shift per child; with every root sharing `Lifetime.Eternal`, terminating 10,000 roots
  took 313 ms and 40,000 took 2.5 s. Each registration is now a slot in the parent; a detaching child
  empties its own slot, and the sequence is compacted once more than half of it is empty. LIFO order,
  exactly-once and the thread-safety guarantees are unchanged. No API change.
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
- **`With` on an already-terminated lifetime now disposes immediately**
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
- Minimum supported Unity version raised to 6000.0 (`unityRelease` `0f1`). The previously declared
  minimums were never verified against a build.
- Package manifest updated to the current Unity package schema: object `author`, a real `description`,
  `licensesUrl`/`documentationUrl`/`changelogUrl` and a `repository` object; the obsolete `category`
  key was removed.
- Assembly definition now declares `rootNamespace` `OpenUGD`, `noEngineReferences: true` and the full
  canonical key set instead of relying on editor defaults.
- README rewritten with install instructions, a quick start and an API overview.
- The package now ships its own test assembly (`Tests/Editor`, 100 tests, gated on `UNITY_INCLUDE_TESTS`).

### Removed
- **`Lifetime.Define(Lifetime, string)`, `Lifetime.Definition.Define(Lifetime, string)` and
  `Lifetime.Definition.Intersection(params Lifetime[])`.** There is now one way to create each kind of
  scope: `lifetime.DefineNested(name)` for a child and `Lifetime.Intersection(...)` for an intersection.
  Migration: `Lifetime.Define(parent, name)` → `parent.DefineNested(name)` (for a definition,
  `definition.Lifetime.DefineNested(name)`); `Lifetime.Definition.Intersection(...)` →
  `Lifetime.Intersection(...)`. A null parent now fails with `NullReferenceException` at the call site
  instead of `ArgumentNullException`.
- **The explicit `void IDisposable.Dispose()` on `Definition`.** It did the same as the public
  `Dispose()`, which already implements the interface. Source- and binary-compatible: a call through
  `IDisposable` now dispatches to the public method.
- **The static `Stack<List<Action>>` pool.** It was popped under `lock(_pool)` in the constructor and
  pushed under the instance lock in `Terminate` — unsynchronized mutation of a shared `Stack` that
  could hand the same `List<Action>` to two live lifetimes. Each lifetime now allocates its own entry
  array, lazily. No API change.
- **Deduplication in `AddDefinition`.** Attaching the same definition to the same parent twice now
  registers twice, which is harmless: `Terminate` is idempotent and each attach registers its own
  detach. Removes another O(n) scan.

### Fixed
- **`Intersection` no longer attaches its definition to `Lifetime.Eternal`**, only to the lifetimes it
  intersects. Every intersection used to be nested in `Eternal` as well, so one that was never terminated
  stayed reachable for the life of the process, and through its detach actions so did the lifetimes it
  intersected and their trees; `Intersection()` of no lifetimes could never be collected at all. Each
  intersection also took a slot, and the lock, of the one process-wide `Eternal`. Termination, the
  born-terminated rule and detaching are unchanged. `Definition.ParentId` of an intersection is now `0`
  (no lifetime has that id) instead of the id of `Eternal`.
- **Sample 08 (Unity Integration) no longer teaches two false things or leaks.** It claimed that the code
  after `await Task.Delay` resumes on a thread-pool thread; started on Unity's main thread it resumes there,
  through `UnitySynchronizationContext`. It claimed that `LifetimeScope`s nest with their GameObjects; they
  are siblings in `Lifetime.Eternal`, which the sample now says, with how to nest for real and what that
  costs on re-parenting. And its lazy `Lifetime` getter created a scope on `Eternal` for an object that was
  never activated, which Unity never sends `OnDestroy`: the scope is now created during activation, reading
  it on an object that has never been active throws, and the README documents that caveat and the
  domain-reload one. The sample has an EditMode test for the guard (`Tests/`).

### Unchanged (explicitly)
- `Lifetime.Terminate()` remains private: only the holder of a `Definition` can end a lifetime.
- Termination actions run in **reverse registration order (LIFO)** — now a documented guarantee.
- There is still **no `RemoveAction`**. To unregister, create a nested definition, register on it, and
  terminate it; that also detaches it from its parent.
- `Lifetime.Id`, `Definition.ParentId` and the renamed `Definition.Name` remain debugging aids with no
  semantics.

## [1.2.0]
### Changed
- LifetimeExtensions.cs

## [1.1.0]
### Added
- LifetimeExtensions.cs

## [1.0.0]
### Added
- Lifetime.cs
