using System;

namespace OpenUGD.Samples.Intersection
{
    /// <summary>
    /// <c>Lifetime.Intersection(a, b, ...)</c> — a scope that ends as soon as <b>any</b> of the given
    /// lifetimes ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DefineNested</c> covers the tree case: one parent, many children. Intersection covers the case
    /// the tree cannot express — something whose validity depends on <i>several</i> independent scopes at
    /// once. A subscription between a UI panel and a network session is valid only while both are alive;
    /// nesting it under either one is wrong, because the other can outlive it.
    /// </para>
    /// <para>
    /// The result is an ordinary <see cref="Lifetime.Definition"/> that <b>you own</b>: you can still
    /// terminate it yourself, earlier than any of the inputs. Until it is terminated it stays attached to
    /// every input lifetime and to <see cref="Lifetime.Eternal"/> — so terminate it when you are done, and
    /// do not create intersections in a loop and abandon them.
    /// </para>
    /// <para>
    /// If any input is <b>already terminated</b>, the returned definition comes back already terminated —
    /// and, per the core invariant, anything you then register on it runs immediately. There is no window
    /// in which an intersection looks alive while one of its inputs is dead.
    /// </para>
    /// <para>
    /// <c>Intersection()</c> with no arguments is the vacuous case: nothing will terminate it on its own,
    /// but its owner still can. A <c>null</c> argument, or a <c>null</c> element, throws
    /// <see cref="ArgumentNullException"/> — and validation happens before any wiring, so a bad call cannot
    /// leave a half-attached definition behind.
    /// </para>
    /// </remarks>
    public static class IntersectionSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            // -------------------------------------------------------------------------------------------
            // 1. Valid only while BOTH are alive. Either one ending is enough to tear it down.
            // -------------------------------------------------------------------------------------------
            log("-- panel x session --");
            var panel = Lifetime.Define(Lifetime.Eternal, "panel");
            var session = Lifetime.Define(Lifetime.Eternal, "session");

            var binding = Lifetime.Intersection(panel.Lifetime, session.Lifetime);
            binding.Lifetime.AddBracket(
                onOpen: () => log("  binding: panel is now showing live session data"),
                onTerminate: () => log("  binding: torn down")
            );

            log("  binding alive: " + binding.Lifetime.IsAlive()); // True

            // The session drops. The panel is still perfectly alive — but the binding is not, because it
            // needed both. Nesting under `panel` would have left a binding pointing at a dead session.
            log("  ...session ends...");
            session.Terminate();

            log("  binding alive: " + binding.Lifetime.IsAlive()); // False
            log("  panel alive:   " + panel.Lifetime.IsAlive());   // True — the panel is unaffected

            panel.Terminate();

            // -------------------------------------------------------------------------------------------
            // 2. The owner can also end an intersection early, before any input ends.
            // -------------------------------------------------------------------------------------------
            log("-- the owner keeps the kill switch --");
            var a = Lifetime.Define(Lifetime.Eternal, "a");
            var b = Lifetime.Define(Lifetime.Eternal, "b");

            var owned = Lifetime.Intersection(a.Lifetime, b.Lifetime);
            owned.Lifetime.AddAction(() => log("  owned: torn down"));

            owned.Terminate(); // neither `a` nor `b` has ended
            log("  a alive: " + a.Lifetime.IsAlive() + ", b alive: " + b.Lifetime.IsAlive()); // True, True

            // Terminating an intersection also detaches it from every input, so a long-lived input does
            // not accumulate entries for intersections that are over.
            a.Terminate();
            b.Terminate();

            // -------------------------------------------------------------------------------------------
            // 3. Intersecting with something already dead yields something already dead.
            // -------------------------------------------------------------------------------------------
            log("-- one dead input is enough --");
            var alive = Lifetime.Define(Lifetime.Eternal, "alive");
            var deadAlready = Lifetime.Define(Lifetime.Eternal, "dead");
            deadAlready.Terminate();

            var stillborn = Lifetime.Intersection(alive.Lifetime, deadAlready.Lifetime);
            log("  intersection terminated on arrival: " + stillborn.IsTerminated); // True

            // ...and so clean-up registered on it runs immediately rather than being dropped. See sample 07.
            stillborn.Lifetime.AddAction(() => log("  registered late, ran immediately"));

            // A bracket, by contrast, acquires nothing here — so it releases nothing. See sample 04.
            stillborn.Lifetime.AddBracket(
                onOpen: () => log("  NEVER RUNS"),
                onTerminate: () => log("  NEVER RUNS EITHER")
            );

            alive.Terminate();

            // -------------------------------------------------------------------------------------------
            // 4. More than two inputs, and the vacuous case.
            // -------------------------------------------------------------------------------------------
            log("-- n-ary and vacuous --");
            var x = Lifetime.Define(Lifetime.Eternal, "x");
            var y = Lifetime.Define(Lifetime.Eternal, "y");
            var z = Lifetime.Define(Lifetime.Eternal, "z");

            using (var all = Lifetime.Intersection(x.Lifetime, y.Lifetime, z.Lifetime))
            {
                all.Lifetime.AddAction(() => log("  three-way intersection torn down"));
                log("  intersection of three, alive: " + all.Lifetime.IsAlive());
            }

            x.Terminate();
            y.Terminate();
            z.Terminate();

            using (var vacuous = Lifetime.Intersection())
            {
                // Nothing will terminate this on its own; only its owner can — which the closing brace does.
                vacuous.Lifetime.AddAction(() => log("  vacuous intersection torn down by its owner"));
            }
        }
    }
}
