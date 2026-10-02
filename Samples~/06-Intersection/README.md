# 06 — `Intersection`: a scope that ends when *either* input ends

```csharp
var binding = Lifetime.Intersection(panel.Lifetime, session.Lifetime);
```

`DefineNested` covers the tree: one parent, many children. Intersection covers what the tree cannot
express — something valid only while **several independent scopes** are alive at once. A binding between a
UI panel and a network session is not a child of either: nest it under the panel and it survives the
session's death; nest it under the session and it survives the panel's.

## What to look at

`IntersectionSample.cs`, four cases:

1. **panel × session** — the session ends, the binding tears down, the panel is untouched.
2. **The owner keeps the kill switch.** The result is an ordinary `Definition` that you own, so you can end
   it earlier than any input. Terminating it also detaches it from every input, so a long-lived input does
   not accumulate entries for intersections that are over.
3. **One already-dead input is enough** — the returned definition is terminated on arrival. Clean-up
   registered on it then runs immediately (sample 07), while a bracket acquires nothing and so releases
   nothing (sample 04).
4. **N-ary and vacuous.** `Intersection()` with no arguments is terminated by nobody but its owner.

## Ownership

Until it is terminated, an intersection stays attached to every input **and to `Lifetime.Eternal`**. Own it
and terminate it — do not create intersections in a loop and abandon them.

A `null` array or a `null` element throws `ArgumentNullException`, and validation runs before any wiring,
so a bad call cannot leave a half-attached definition behind.

This sample is plain C# — the package references neither `UnityEngine` nor `UnityEditor`.
