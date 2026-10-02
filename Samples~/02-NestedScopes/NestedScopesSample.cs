using System;

namespace OpenUGD.Samples.NestedScopes
{
    /// <summary>
    /// Scopes form a tree, and the tree collapses downwards: terminating a parent terminates every child,
    /// recursively, without anyone having written that wiring by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>parent.DefineNested("child")</c> creates a child Definition and registers the child's termination
    /// on the parent. So:
    /// </para>
    /// <list type="bullet">
    /// <item><description>parent terminates -&gt; every child terminates (the cascade);</description></item>
    /// <item><description>child terminates first -&gt; it <i>unregisters itself</i> from the parent, so a
    /// long-lived parent with thousands of short-lived children does not accumulate dead entries. This is
    /// why "one Definition per request" is a fine pattern even under a Definition that lives for the whole
    /// process.</description></item>
    /// </list>
    /// <para>
    /// Note the ordering guarantee that falls out of this: clean-up runs LIFO, so a child registered late
    /// tears down before anything the parent registered earlier. The last thing built is the first thing
    /// destroyed — exactly like a stack.
    /// </para>
    /// <para>
    /// Also note <c>DefineNested</c> on an already-terminated lifetime returns a definition that is
    /// <b>born terminated</b> — the same rule as registering on a dead scope (sample 07). It never hands back
    /// a live child of a dead parent, and it does not throw either.
    /// </para>
    /// </remarks>
    public static class NestedScopesSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            // The root of this sample's tree. Lifetime.Eternal never terminates, so it is the natural
            // parent for anything that lives as long as the process.
            var app = Lifetime.Eternal.DefineNested("app");
            app.Lifetime.AddAction(() => log("app: closed"));

            // A child of the app. It dies when the app dies — nothing had to be written to make that so.
            var session = app.Lifetime.DefineNested("session");
            session.Lifetime.AddAction(() => log("session: closed"));

            // Two grandchildren of the app, children of the session.
            var requestA = session.Lifetime.DefineNested("request-A");
            requestA.Lifetime.AddAction(() => log("request-A: closed"));

            var requestB = session.Lifetime.DefineNested("request-B");
            requestB.Lifetime.AddAction(() => log("request-B: closed"));

            log("built: app > session > (request-A, request-B)");

            // ---------------------------------------------------------------------------------------
            // 1. A child can end on its own, early, without touching anything above it.
            // ---------------------------------------------------------------------------------------
            log("-- terminating request-A on its own --");
            requestA.Terminate();

            log("request-A terminated: " + requestA.IsTerminated); // True
            log("session still alive:  " + session.Lifetime.IsAlive()); // True — children do not kill parents
            log("request-B still alive: " + requestB.Lifetime.IsAlive()); // True — nor siblings

            // request-A has also removed itself from the session's action list at this point, so the
            // session is not holding a dead entry. Long-lived parents stay clean.

            // ---------------------------------------------------------------------------------------
            // 2. Terminating the app cascades all the way down, in LIFO order.
            // ---------------------------------------------------------------------------------------
            log("-- terminating the app root --");
            app.Terminate();

            log("session terminated:   " + session.IsTerminated);   // True — cascaded
            log("request-B terminated: " + requestB.IsTerminated);  // True — cascaded, two levels down

            // ---------------------------------------------------------------------------------------
            // 3. Nesting under something that has already ended yields something already ended.
            // ---------------------------------------------------------------------------------------
            var tooLate = app.Lifetime.DefineNested("too-late");
            log("child of a terminated lifetime born terminated: " + tooLate.IsTerminated); // True

            // ...so, as on any terminated lifetime, clean-up registered on it runs immediately.
            tooLate.Lifetime.AddAction(() => log("too-late: clean-up ran immediately"));
        }
    }
}
