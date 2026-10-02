using System;

namespace OpenUGD.Samples.NestedScopes
{
    /// <summary>
    /// Two guarantees you are allowed to rely on when a scope ends: clean-up runs <b>LIFO</b>, and one
    /// throwing action <b>does not strand</b> the others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>LIFO.</b> Termination actions run in reverse registration order. This is a documented guarantee,
    /// not an implementation detail — the last resource acquired is the first one released, which is the
    /// only order that is correct when later resources were built on top of earlier ones.
    /// </para>
    /// <para>
    /// <b>Failure isolation (2.0.0).</b> If a termination action throws, the remaining actions still run,
    /// and the failure is reported only after the lifetime is fully terminated. A buggy teardown can no
    /// longer silently skip everything registered before it. One failure is rethrown <i>as itself</i>,
    /// with its original type and stack trace; two or more are thrown together as one
    /// <see cref="AggregateException"/> — call <see cref="AggregateException.Flatten"/> for the leaves when
    /// scopes nest.
    /// </para>
    /// <para>
    /// Because <c>Definition.Dispose()</c> is just <c>Terminate()</c>, that exception can be thrown at the
    /// closing brace of a <c>using</c> block. Wrap the block if teardown failures are something you intend
    /// to handle.
    /// </para>
    /// </remarks>
    public static class TerminationOrderSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            log("-- LIFO --");
            using (var scope = Lifetime.Eternal.DefineNested("lifo"))
            {
                scope.Lifetime.AddAction(() => log("  released 1 (registered first, runs last)"));
                scope.Lifetime.AddAction(() => log("  released 2"));
                scope.Lifetime.AddAction(() => log("  released 3 (registered last, runs first)"));
            }

            log("-- one throwing action does not abort the rest --");
            var definition = Lifetime.Eternal.DefineNested("failing");
            definition.Lifetime.AddAction(() => log("  clean-up A ran"));
            definition.Lifetime.AddAction(() => throw new InvalidOperationException("teardown B failed"));
            definition.Lifetime.AddAction(() => log("  clean-up C ran"));

            try
            {
                definition.Terminate();
            }
            catch (InvalidOperationException exception)
            {
                // Note what already happened before this catch: C ran, B threw, A ran anyway, and the
                // lifetime is fully terminated. Only the reporting was deferred to here — and because B
                // was the only failure, it arrives as itself, not wrapped in an AggregateException.
                log("  reported after the fact: " + exception.Message);
            }

            log("terminated despite the failure: " + definition.IsTerminated); // True

            // Terminate is idempotent: a second call neither re-runs the actions nor re-throws.
            definition.Terminate();
            log("second Terminate() was a no-op");

            log("-- two or more failures arrive together --");
            var several = Lifetime.Eternal.DefineNested("several-failing");
            several.Lifetime.AddAction(() => throw new InvalidOperationException("teardown X failed"));
            several.Lifetime.AddAction(() => log("  clean-up Y ran"));
            several.Lifetime.AddAction(() => throw new TimeoutException("teardown Z failed"));

            try
            {
                several.Terminate();
            }
            catch (AggregateException exception)
            {
                // In the order they occurred: Z (registered last) first, then X.
                foreach (var inner in exception.Flatten().InnerExceptions)
                {
                    log("  reported after the fact: " + inner.Message);
                }
            }
        }
    }
}
