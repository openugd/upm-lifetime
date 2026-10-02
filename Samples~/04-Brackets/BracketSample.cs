using System;

namespace OpenUGD.Samples.Brackets
{
    /// <summary>
    /// <c>AddBracket(onOpen, onTerminate)</c> — acquire now, release when the scope ends, written as one
    /// expression so the two halves cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bracket is the answer to the oldest bug in resource handling: the acquire and the release live at
    /// two different places in the file, and someone edits one of them. With a bracket they are one call,
    /// on one line, reviewed together.
    /// </para>
    /// <para>
    /// <b>Semantics.</b> <c>onOpen</c> runs immediately, and only then is <c>onTerminate</c> registered —
    /// so a teardown can never be armed for a resource that was not acquired. If <c>onOpen</c> throws,
    /// nothing is registered and the exception propagates. <i>(The order was the other way round before
    /// 2.0.0.)</i>
    /// </para>
    /// <para>
    /// <b>On an already-terminated lifetime neither callback runs.</b> This is the one place where the
    /// package deliberately does <i>not</i> run your callback immediately, and the asymmetry with
    /// <c>AddAction</c> is intended: a bracket's resource does not exist until <c>onOpen</c> runs, so if
    /// the scope has already ended nothing was ever acquired and there is nothing to release. The single
    /// invariant across both methods is: <b>every acquired resource is released exactly once.</b>
    /// </para>
    /// <para>
    /// <c>onOpen</c> may be <c>null</c>, which reduces the call to "register <c>onTerminate</c>, but only
    /// if the scope is still open".
    /// </para>
    /// </remarks>
    public static class BracketSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            log("-- brackets close LIFO, interleaved with plain actions in one sequence --");
            using (var scope = Lifetime.Define(Lifetime.Eternal, "brackets"))
            {
                Lifetime lifetime = scope.Lifetime;

                lifetime.AddBracket(
                    onOpen: () => log("  file: opened"),
                    onTerminate: () => log("  file: closed")
                );

                // A plain action registered between two brackets takes its place in the same LIFO
                // sequence — brackets and actions are not two separate stacks.
                lifetime.AddAction(() => log("  (plain action)"));

                lifetime.AddBracket(
                    onOpen: () => log("  lock: taken"),
                    onTerminate: () => log("  lock: released")
                );

                log("  ...working...");
            }
            // Output order on close: lock released, plain action, file closed.

            log("-- AddBracket returns the lifetime, so brackets chain --");
            using (var scope = Lifetime.Define(Lifetime.Eternal, "chained"))
            {
                scope.Lifetime
                    .AddBracket(() => log("  A: on"), () => log("  A: off"))
                    .AddBracket(() => log("  B: on"), () => log("  B: off"))
                    .AddAction(() => log("  C: done"));
            }

            log("-- a bracket on a dead scope acquires nothing, so it releases nothing --");
            var dead = Lifetime.Define(Lifetime.Eternal, "dead");
            dead.Terminate();

            dead.Lifetime.AddBracket(
                onOpen: () => log("  THIS NEVER RUNS"),
                onTerminate: () => log("  NEITHER DOES THIS")
            );
            log("  nothing was acquired, nothing was released — as intended");

            // Contrast with AddAction on the same dead lifetime, which refers to something the caller
            // already holds and therefore MUST still run. See sample 07.
            dead.Lifetime.AddAction(() => log("  AddAction on a dead scope ran immediately"));

            log("-- if onOpen throws, nothing is registered --");
            using (var scope = Lifetime.Define(Lifetime.Eternal, "throwing-open"))
            {
                try
                {
                    scope.Lifetime.AddBracket(
                        onOpen: () => throw new InvalidOperationException("could not acquire"),
                        onTerminate: () => log("  THIS NEVER RUNS: the resource was never acquired")
                    );
                }
                catch (InvalidOperationException exception)
                {
                    log("  onOpen threw: " + exception.Message + " — no teardown was armed");
                }
            }
        }
    }
}
