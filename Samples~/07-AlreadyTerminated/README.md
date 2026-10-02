# 07 — Registering on an already-terminated scope runs immediately

The 2.0.0 rule, and the reason for it.

> Every action handed to a lifetime runs **exactly once**. Alive → it runs at termination. Already
> terminated → it runs right now, on the calling thread, inside the `AddAction` call. There is no state in
> which it is silently dropped.

## Why this removes a class of leaks

Before 2.0.0, registering on a dead lifetime was a silent no-op. The failure mode was not a crash but an
**absence**: the socket stayed open, the subscription stayed subscribed, the `IDisposable` was never
disposed, the `CancellationToken` never cancelled and every `await` on it hung forever. Nothing threw,
nothing logged, and the symptom appeared hours later somewhere else.

And it was not a niche case. "The scope died between constructing the resource and registering its
clean-up" is the normal outcome of an ordinary race — a cancelled load, a closed window, a failed sibling —
not a programming error. Correctness used to depend on winning that race.

## What it buys you at the call site

You never write this again:

```csharp
if (!lifetime.IsTerminated) lifetime.AddAction(Release);  // broken: racy, and loses the action
```

Just `lifetime.AddAction(Release);`. The guard was never correct anyway — the lifetime can terminate
between the check and the call.

## What to look at

`AlreadyTerminatedSample.cs`:

- `Run` — the action running *between* the "before" and "after" log lines, i.e. inside the call.
- `With` disposing immediately, and `AsCancellationToken` returning a pre-cancelled token.
- `RaceWithTermination` — the ordinary sequence that the old behaviour got wrong.
- The last block: an exception from an immediate invocation propagates straight out of `AddAction`, as an
  ordinary exception rather than an `AggregateException` — nothing is being torn down, this ran inline.

## The one deliberate exception

`AddBracket` does **not** run either callback on a dead lifetime: nothing was acquired, so there is nothing
to release. See sample 04. Both rules serve one invariant — *every acquired resource is released exactly
once.*

This sample is plain C# — the package references neither `UnityEngine` nor `UnityEditor`.
