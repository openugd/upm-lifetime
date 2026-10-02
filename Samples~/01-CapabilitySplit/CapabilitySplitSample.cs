using System;

namespace OpenUGD.Samples.CapabilitySplit
{
    /// <summary>
    /// The single most important idea in this package: <b>a Lifetime cannot be ended by whoever holds it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Lifetime.Terminate</c> is private. The only public way to end a scope is
    /// <c>Lifetime.Definition.Terminate()</c> (or <c>Dispose()</c>), and you only get a Definition by
    /// creating one. So there are exactly two roles:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The <b>owner</b> holds the <see cref="Lifetime.Definition"/>. It created the scope, it decides when
    /// the scope ends, and it is the only one that can end it.
    /// </description></item>
    /// <item><description>
    /// A <b>consumer</b> holds the <see cref="Lifetime"/>. It may ask "are you still alive?" and it may say
    /// "run this when you die". It may not kill the scope, and it may not resurrect it.
    /// </description></item>
    /// </list>
    /// <para>
    /// This is the same split as <c>CancellationTokenSource</c> (owner) versus <c>CancellationToken</c>
    /// (consumer). Handing out a Lifetime hands out no authority — which is why you can pass one to any
    /// number of subsystems without auditing what they do with it.
    /// </para>
    /// <para>
    /// Run <see cref="Run"/> and read the log: the connection registers its own clean-up, and the owner —
    /// and only the owner — makes that clean-up happen.
    /// </para>
    /// </remarks>
    public static class CapabilitySplitSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            log("-- creating the owner (it holds the Definition) --");

            // Session is the owner. Nobody outside it ever sees its Definition.
            var session = new Session(Lifetime.Eternal, log);

            // The connection is a consumer. It gets a Lifetime and nothing else.
            var connection = new Connection(session.Lifetime, log);

            connection.Send("hello");

            log("-- owner ends the scope --");

            // Only this line can end the scope. There is no other line in this file that could.
            session.Dispose();

            log("session alive: " + session.Lifetime.IsAlive()); // False

            // The consumer can still be called; it now guards on its own dead lifetime.
            connection.Send("too late");
        }

        /// <summary>
        /// The OWNER. It keeps the <see cref="Lifetime.Definition"/> private and exposes only the
        /// <see cref="Lifetime"/>. That is the whole pattern: expose the capability to observe, keep the
        /// capability to terminate.
        /// </summary>
        private sealed class Session : IDisposable
        {
            private readonly Lifetime.Definition _definition;

            public Session(Lifetime parent, Action<string> log)
            {
                // The owner creates the scope, so the owner is the one that can end it.
                _definition = parent.DefineNested("session");

                log($"Session: opened (lifetime #{_definition.Lifetime.Id}, parent #{_definition.ParentId})");
                _definition.Lifetime.AddAction(() => log("Session: closed"));
            }

            /// <summary>
            /// What everyone else is allowed to see. Note the return type: <see cref="Lifetime"/>, never
            /// <see cref="Lifetime.Definition"/>. Returning the Definition would hand out the kill switch.
            /// </summary>
            public Lifetime Lifetime => _definition.Lifetime;

            // Definition.Dispose() is just Terminate(), so an owner can forward its own Dispose to it.
            public void Dispose() => _definition.Dispose();
        }

        /// <summary>
        /// A CONSUMER. It is handed a <see cref="Lifetime"/> and can do exactly two useful things with it:
        /// observe whether it is alive, and register clean-up on it.
        /// </summary>
        private sealed class Connection
        {
            private readonly Lifetime _lifetime;
            private readonly Action<string> _log;

            public Connection(Lifetime lifetime, Action<string> log)
            {
                _lifetime = lifetime;
                _log = log;

                log("Connection: opened");

                // The consumer's whole authority: "when you end, run this".
                lifetime.AddAction(() => log("Connection: closed"));

                // The line below does not compile, and that is the point of this sample:
                //
                //     lifetime.Terminate();
                //
                // Terminate() is private on Lifetime. The consumer was given no Definition, so it has no
                // way to end a scope it does not own. Contrast this with passing around an IDisposable,
                // where any holder can dispose it out from under everybody else.
            }

            public void Send(string message)
            {
                // IsAlive() is the positive reading of IsTerminated, for call sites where "!" reads badly.
                if (!_lifetime.IsAlive())
                {
                    _log($"Connection: dropped '{message}' (scope has ended)");
                    return;
                }

                _log($"Connection: sent '{message}'");
            }
        }
    }
}
