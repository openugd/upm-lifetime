using System;
using System.Threading;

namespace OpenUGD.Samples.AlreadyTerminated
{
    /// <summary>
    /// The 2.0.0 rule: <b>registering clean-up on an already-terminated scope runs it immediately.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invariant is: <i>every action handed to a lifetime runs exactly once.</i> If the lifetime is
    /// alive, the action runs at termination. If the lifetime is already dead, the action runs right now,
    /// on the calling thread, inside the <c>AddAction</c> call, before it returns. There is no third state
    /// in which the action is quietly discarded.
    /// </para>
    /// <para>
    /// <b>Why this removes a whole class of leaks.</b> Before 2.0.0, registering on a dead lifetime was a
    /// silent no-op, and the failure mode was not a crash but an absence: the socket stayed open, the
    /// subscription stayed subscribed, the <c>IDisposable</c> was never disposed, the
    /// <see cref="CancellationToken"/> never cancelled and every <c>await</c> on it hung forever. Nothing
    /// threw, nothing logged, and the bug showed up hours later as a leak somewhere else entirely.
    /// </para>
    /// <para>
    /// <b>Why this is not a niche case.</b> "The scope is dead by the time you register" is the normal
    /// outcome of an ordinary race, not a programming error — see <see cref="RaceWithTermination"/> below.
    /// Under the old rule, correctness depended on winning that race.
    /// </para>
    /// <para>
    /// <b>What it buys you at the call site.</b> You never have to write
    /// <c>if (!lifetime.IsTerminated) lifetime.AddAction(...)</c>. That guard was always broken anyway: the
    /// lifetime can terminate between the check and the call, and then the action is lost. Just register.
    /// </para>
    /// <para>
    /// The two consequences to keep in mind: the action may run <i>before</i> <c>AddAction</c> returns, so
    /// do not register something that assumes the surrounding constructor has finished; and an exception it
    /// throws propagates straight to the caller of <c>AddAction</c> rather than into an
    /// <see cref="AggregateException"/> at termination.
    /// </para>
    /// </remarks>
    public static class AlreadyTerminatedSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            var dead = Lifetime.Eternal.DefineNested("dead");
            dead.Terminate();
            Lifetime lifetime = dead.Lifetime;

            log("-- AddAction on a dead scope --");
            log("  before AddAction");
            lifetime.AddAction(() => log("  >>> ran INSIDE the AddAction call"));
            log("  after AddAction");

            log("-- so does everything built on top of it --");

            // With(): the object already exists, so it is disposed now instead of leaking.
            new Handle("socket", log).With(lifetime);

            // AsCancellationToken(): a pre-cancelled token instead of one that can never fire.
            var token = lifetime.AsCancellationToken();
            log("  token.IsCancellationRequested: " + token.IsCancellationRequested); // True

            log("-- the guard you no longer need to write --");
            RegisterCleanup(lifetime, log);

            log("-- and the race it never handled correctly --");
            RaceWithTermination(log);

            log("-- caveat: exceptions surface at the registration site --");
            try
            {
                lifetime.AddAction(() => throw new InvalidOperationException("clean-up failed"));
            }
            catch (InvalidOperationException exception)
            {
                // Not an AggregateException: nothing is being torn down, this ran inline.
                log("  thrown straight out of AddAction: " + exception.Message);
            }
        }

        /// <summary>
        /// The whole point at a call site: no guard, no branch, no "did I win the race" reasoning.
        /// </summary>
        private static void RegisterCleanup(Lifetime lifetime, Action<string> log)
        {
            // NOT this:
            //     if (!lifetime.IsTerminated) lifetime.AddAction(Release);   // broken: racy, and drops
            //                                                                // the action when it loses
            // Just this:
            lifetime.AddAction(() => log("  released — no IsTerminated check anywhere"));
        }

        /// <summary>
        /// A perfectly ordinary sequence in which the scope is already dead by the time a consumer gets to
        /// register: the consumer was constructed, then something else ended the scope, then the consumer
        /// finished wiring itself up. Nobody made a mistake. Under the old rule the clean-up was lost.
        /// </summary>
        private static void RaceWithTermination(Action<string> log)
        {
            var definition = Lifetime.Eternal.DefineNested("racy");
            Lifetime lifetime = definition.Lifetime;

            // Step 1: a consumer starts building. It captures the lifetime; it does not yet register.
            log("  consumer: acquiring the resource");
            var handle = new Handle("db-connection", log);

            // Step 2: meanwhile, someone else ends the scope — a cancelled load, a closed window, a
            // failed sibling. This is the normal course of events, not a bug.
            log("  owner: terminating the scope");
            definition.Terminate();

            // Step 3: the consumer finishes wiring itself up. It has no idea about step 2, and it should
            // not have to. The connection is closed right here, exactly once.
            handle.With(lifetime);
            log("  consumer: registration done; the resource is already released");
        }

        private sealed class Handle : IDisposable
        {
            private readonly string _name;
            private readonly Action<string> _log;

            public Handle(string name, Action<string> log)
            {
                _name = name;
                _log = log;
                log($"  {name}: acquired");
            }

            public void Dispose() => _log($"  {_name}: disposed");
        }
    }
}
