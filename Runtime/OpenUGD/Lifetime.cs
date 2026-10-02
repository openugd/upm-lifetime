using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenUGD
{
    /// <summary>
    /// A scope: an object that says "this is still alive", and that runs registered clean-up
    /// exactly once when the scope ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Capability split.</b> A <see cref="Lifetime"/> can only be observed, and have clean-up registered
    /// on it. It cannot be ended: only the holder of the owning <see cref="Definition"/> can do that, via
    /// <see cref="Definition.Terminate"/> or <see cref="Definition.Dispose"/>. This is the same split as
    /// <c>CancellationTokenSource</c> versus <see cref="CancellationToken"/>, and it is deliberate — handing
    /// out a <see cref="Lifetime"/> hands out no authority to kill it.
    /// </para>
    /// <para>
    /// <b>Core invariant.</b> Every action handed to a lifetime runs exactly once. If the lifetime is alive,
    /// the action runs at termination; if the lifetime is already terminated, the action runs immediately,
    /// inside the <see cref="AddAction"/> call. There is no state in which an action is silently dropped.
    /// </para>
    /// <para>
    /// <b>Order.</b> Termination actions run in reverse registration order (LIFO). This is a guarantee, not
    /// an implementation detail: the last resource acquired is the first one released.
    /// </para>
    /// <para>
    /// <b>Thread safety.</b> Every member is safe to call concurrently. No lock is ever held while user code
    /// runs, so a termination action may freely call back into this lifetime, its parent, or its children.
    /// </para>
    /// <para>
    /// <b>Breaking changes in 2.0.0.</b> <see cref="AddAction"/> now invokes immediately on a terminated
    /// lifetime instead of silently dropping the action, and no longer rejects duplicate delegates;
    /// <see cref="AddBracket"/> now invokes <c>onOpen</c> before registering <c>onTerminate</c>; a throwing
    /// termination action no longer aborts the remaining ones, and termination reports failures as an
    /// <see cref="AggregateException"/>. See the package CHANGELOG.
    /// </para>
    /// </remarks>
    public class Lifetime
    {
        /// <summary>
        /// The owning handle of a <see cref="OpenUGD.Lifetime"/>: the only thing that can end it.
        /// Hand the <see cref="Lifetime"/> out; keep the <see cref="Definition"/> yourself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Definition"/> implements <see cref="IDisposable"/>, so it composes with <c>using</c>
        /// and <c>using var</c>. <see cref="Dispose"/> is an ordinary public method rather than an explicit
        /// interface implementation, so <c>using</c> on a <see cref="Definition"/> local does not box.
        /// </para>
        /// <code>
        /// using var scope = lifetime.DefineNested("load-level");
        /// Load(scope); // implicit conversion Definition -&gt; Lifetime
        /// </code>
        /// </remarks>
        public class Definition : IDisposable
        {
            private Definition(string id, int parentId)
            {
                Id = id;
                ParentId = parentId;
                Lifetime = new Lifetime();
            }

            /// <summary>
            /// [used for debugging purposes]
            /// The optional human-readable name given at definition time. Carries no semantics and is not
            /// required to be unique; it exists only to make a lifetime tree readable in a debugger or a
            /// log. May be <c>null</c>.
            /// </summary>
            public string Id { get; }

            /// <summary>
            /// [used for debugging purposes]
            /// The <see cref="OpenUGD.Lifetime.Id"/> of the lifetime this definition was defined on.
            /// Carries no semantics; it exists only to make the lifetime tree readable while debugging.
            /// For a definition produced by <see cref="Intersection"/> this is the id of
            /// <see cref="OpenUGD.Lifetime.Eternal"/>, not of any of the intersected lifetimes.
            /// </summary>
            public int ParentId { get; }

            /// <summary>
            /// The lifetime owned by this definition.
            /// </summary>
            public Lifetime Lifetime { get; }

            /// <summary>
            /// Whether the owned lifetime has already been terminated. Once <c>true</c>, always <c>true</c>.
            /// </summary>
            public bool IsTerminated => Lifetime.IsTerminated;

            /// <summary>
            /// Ends the lifetime, running every registered action once, in reverse registration order (LIFO).
            /// </summary>
            /// <remarks>
            /// <para>
            /// Idempotent: the second and later calls do nothing.
            /// </para>
            /// <para>
            /// If one or more actions throw, all of them still run and a single
            /// <see cref="AggregateException"/> is thrown at the end; the lifetime is fully terminated
            /// either way, and a later call neither re-runs the actions nor re-throws. Nested scopes produce
            /// nested aggregates — call <see cref="AggregateException.Flatten"/> if you want the leaves.
            /// </para>
            /// <para>
            /// Concurrency: exactly one caller runs the actions. Other concurrent callers return
            /// <i>immediately</i>, possibly before that caller has finished, and never block. If you need
            /// "clean-up has finished", be the thread that owns the definition.
            /// </para>
            /// </remarks>
            /// <exception cref="AggregateException">One or more termination actions threw.</exception>
            public void Terminate() => Lifetime.Terminate();

            /// <summary>
            /// Same as <see cref="Terminate"/>; lets a definition be used with <c>using</c>.
            /// </summary>
            /// <remarks>
            /// Note that this can therefore throw at the closing brace of a <c>using</c> block; see
            /// <see cref="Terminate"/>.
            /// </remarks>
            /// <exception cref="AggregateException">One or more termination actions threw.</exception>
            public void Dispose() => Lifetime.Terminate();

            /// <summary>
            /// Converts a definition to the lifetime it owns, so scope code never has to write
            /// <c>.Lifetime</c>. Null-safe.
            /// </summary>
            /// <param name="definition">The definition to convert; may be <c>null</c>.</param>
            /// <returns>
            /// The owned <see cref="OpenUGD.Lifetime"/>, or <c>null</c> if <paramref name="definition"/>
            /// is <c>null</c>.
            /// </returns>
            public static implicit operator Lifetime(Definition definition) => definition?.Lifetime;

            /// <summary>
            /// Creates a definition whose lifetime is nested in <paramref name="lifetime"/>: when
            /// <paramref name="lifetime"/> terminates, the new lifetime terminates too. Terminating the new
            /// definition first unregisters it from <paramref name="lifetime"/>, so a long-lived parent with
            /// many short-lived children does not accumulate dead entries.
            /// </summary>
            /// <param name="lifetime">The parent lifetime. Must not be <c>null</c> and must be alive.</param>
            /// <param name="id">[used for debugging purposes] Optional name for the new definition.</param>
            /// <returns>The new definition. The caller owns it and is responsible for terminating it.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="lifetime"/> is <c>null</c>.</exception>
            /// <exception cref="InvalidOperationException">
            /// <paramref name="lifetime"/> is already terminated. Note the inherent race: if
            /// <paramref name="lifetime"/> terminates concurrently, just after the check, the call succeeds
            /// and returns a definition that is already terminated instead of throwing. Either way a live
            /// child of a dead parent is never produced.
            /// </exception>
            public static Definition Define(Lifetime lifetime, string id = null)
            {
                if (lifetime == null)
                    throw new ArgumentNullException(nameof(lifetime),
                        $"{nameof(lifetime)} can't be null on define new definition");
                if (lifetime.IsTerminated)
                    throw new InvalidOperationException(
                        $"{nameof(lifetime)} can't be terminated on define new definition");

                var definition = new Definition(id, lifetime.Id);
                lifetime.AddDefinition(definition);
                return definition;
            }

            /// <summary>
            /// Creates a definition whose lifetime terminates as soon as <i>any</i> of
            /// <paramref name="lifetimes"/> terminates — the intersection of all of them. If one of them is
            /// already terminated, the returned definition is already terminated.
            /// </summary>
            /// <param name="lifetimes">
            /// The lifetimes to intersect. An empty array yields the vacuous intersection: a definition that
            /// nothing will terminate on its own, but that its owner can still terminate.
            /// </param>
            /// <returns>
            /// The new definition. The caller owns it: until it is terminated it stays attached to every
            /// lifetime in <paramref name="lifetimes"/> and to <see cref="OpenUGD.Lifetime.Eternal"/>.
            /// </returns>
            /// <exception cref="ArgumentNullException">
            /// <paramref name="lifetimes"/>, or any element of it, is <c>null</c>.
            /// </exception>
            public static Definition Intersection(params Lifetime[] lifetimes)
            {
                if (lifetimes == null)
                    throw new ArgumentNullException(nameof(lifetimes),
                        $"{nameof(lifetimes)} can't be null on define an intersection");

                // Validate everything before wiring anything up, so a bad argument cannot leave a
                // half-built definition attached to some of the lifetimes (and to Eternal, forever).
                foreach (var lifetime in lifetimes)
                {
                    if (lifetime == null)
                        throw new ArgumentNullException(nameof(lifetimes),
                            $"{nameof(lifetimes)} can't contain null on define an intersection");
                }

                var definition = Define(Eternal);
                foreach (var lifetime in lifetimes)
                {
                    lifetime.AddDefinition(definition);
                }

                return definition;
            }
        }

        private static int _instances;

        /// <summary>
        /// A lifetime that never terminates. Use it as the root of a lifetime tree and as the parent of
        /// scopes that live as long as the process.
        /// </summary>
        /// <remarks>
        /// Anything registered directly on <see cref="Eternal"/> is kept alive forever and never runs, so
        /// register on a nested definition instead whenever the thing being registered can go away earlier.
        /// </remarks>
        public static readonly Lifetime Eternal = new Lifetime();

        // The one and only lock of this type. It is taken exclusively around reads and writes of the two
        // fields below - never around a call to user code, and never around a call into another Lifetime.
        // Every lock block in this file is a handful of straight-line field operations; verify by eye.
        private readonly object _lock = new object();
        private readonly int _id;

        // Registered actions. Null means "none": either nothing has been registered yet (allocation is
        // deferred) or the list has been handed off to Terminate. Only ever touched under _lock.
        private List<Action> _actions;

        // Written under _lock, read anywhere. Volatile only so that the lock-free read in IsTerminated
        // cannot be hoisted or reordered; the correctness of AddAction and Terminate does not depend on it,
        // because both of them read and write this field under _lock.
        private volatile bool _isTerminated;

        private Lifetime()
        {
            _id = Interlocked.Increment(ref _instances);
        }

        /// <summary>
        /// [used for debugging purposes]
        /// A process-unique, monotonically increasing number identifying this lifetime instance. Carries no
        /// semantics; it exists only to make lifetime trees readable while debugging. Stable across
        /// termination.
        /// </summary>
        public int Id => _id;

        /// <summary>
        /// Whether this lifetime has terminated. Once <c>true</c>, always <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Already <c>true</c> while the termination actions are running, so an action always observes its
        /// own lifetime as dead. In concurrent code a <c>false</c> result may be stale by the time you act
        /// on it; that is fine by design, because <see cref="AddAction"/> and <see cref="AddBracket"/> are
        /// correct whichever way the race falls. Prefer calling them over testing this first.
        /// </remarks>
        public bool IsTerminated => _isTerminated;

        /// <summary>
        /// Creates a definition whose lifetime is nested in <paramref name="lifetime"/>.
        /// Shorthand for <see cref="Definition.Define"/>.
        /// </summary>
        /// <param name="lifetime">The parent lifetime. Must not be <c>null</c> and must be alive.</param>
        /// <param name="id">[used for debugging purposes] Optional name for the new definition.</param>
        /// <returns>The new definition. The caller owns it and is responsible for terminating it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="lifetime"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="lifetime"/> is already terminated.</exception>
        public static Definition Define(Lifetime lifetime, string id = null) => Definition.Define(lifetime, id);

        /// <summary>
        /// Creates a definition whose lifetime terminates as soon as <i>any</i> of
        /// <paramref name="lifetimes"/> terminates. Shorthand for <see cref="Definition.Intersection"/>.
        /// </summary>
        /// <param name="lifetimes">The lifetimes to intersect.</param>
        /// <returns>The new definition.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="lifetimes"/>, or any element of it, is <c>null</c>.
        /// </exception>
        public static Definition Intersection(params Lifetime[] lifetimes) => Definition.Intersection(lifetimes);

        /// <summary>
        /// Creates a definition whose lifetime is nested in this one.
        /// </summary>
        /// <param name="name">[used for debugging purposes] Optional name for the new definition.</param>
        /// <returns>The new definition. The caller owns it and is responsible for terminating it.</returns>
        /// <exception cref="InvalidOperationException">This lifetime is already terminated.</exception>
        public Definition DefineNested(string name = null) => Define(this, name);

        /// <summary>
        /// Registers an action to run when this lifetime terminates — or runs it right now, if this lifetime
        /// has already terminated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>If this lifetime is already terminated the action is invoked immediately</b>, on the calling
        /// thread, before this method returns, and is not stored. This is what guarantees the core
        /// invariant: every action handed to a lifetime runs exactly once, no matter when it was handed
        /// over, so callers never need to test <see cref="IsTerminated"/> first. An exception thrown by that
        /// immediate invocation propagates to the caller.
        /// <i>Changed in 2.0.0</i> — the action used to be silently dropped, which quietly broke everything
        /// built on top: disposables that were never disposed, cancellation tokens that never cancelled,
        /// subscriptions that were never made.
        /// </para>
        /// <para>
        /// <b>Duplicates are allowed.</b> Registering the same delegate twice registers it twice and runs it
        /// twice, exactly like ordinary .NET multicast delegates.
        /// <i>Changed in 2.0.0</i> — this used to throw <see cref="ArgumentException"/>, at the cost of an
        /// O(n) scan on every single registration.
        /// </para>
        /// <para>
        /// <b>Order.</b> Actions run in reverse registration order (LIFO).
        /// </para>
        /// <para>
        /// <b>To unregister</b> there is deliberately no <c>RemoveAction</c>. Use a nested definition and
        /// terminate it — that composes, it cannot leave a dangling entry, and terminating the nested
        /// definition also removes it from this lifetime:
        /// </para>
        /// <code>
        /// var subscription = lifetime.DefineNested("subscription");
        /// subscription.Lifetime.AddAction(Unsubscribe);
        /// // ...later, to unregister early:
        /// subscription.Terminate();
        /// </code>
        /// <para>
        /// No lock is held while the action runs, so it may safely call back into this lifetime — including
        /// registering further actions, which then run immediately.
        /// </para>
        /// </remarks>
        /// <param name="action">The action to run. Must not be <c>null</c>.</param>
        /// <returns>This lifetime, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="action"/> is <c>null</c>.</exception>
        public Lifetime AddAction(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action), $"{nameof(action)} can't be null");

            lock (_lock)
            {
                if (!_isTerminated)
                {
                    // Lazily allocated: a lifetime that never receives an action never allocates a list.
                    (_actions ??= new List<Action>()).Add(action);
                    return this;
                }
            }

            // Already terminated. Invoked outside the lock, for the same reason Terminate invokes outside
            // the lock: user code must never run while we hold it.
            action();
            return this;
        }

        /// <summary>
        /// Acquires a resource now and releases it when this lifetime terminates: invokes
        /// <paramref name="onOpen"/> immediately, then registers <paramref name="onTerminate"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>If this lifetime is already terminated, neither callback runs.</b> A bracket acquires a
        /// resource and releases it; if the scope has already ended the resource was never acquired, so
        /// there is nothing to release. This is consistent with <see cref="AddAction"/> under a single
        /// invariant: every <i>acquired</i> resource is released exactly once. The asymmetry is real and
        /// intended — an action handed to <see cref="AddAction"/> refers to something the caller already
        /// holds, whereas a bracket's resource does not exist until <paramref name="onOpen"/> runs.
        /// </para>
        /// <para>
        /// <paramref name="onOpen"/> runs <i>before</i> <paramref name="onTerminate"/> is registered, so a
        /// teardown can never be armed for a resource that was not acquired. If <paramref name="onOpen"/>
        /// throws, nothing is registered and the exception propagates.
        /// <i>Changed in 2.0.0</i> — the registration used to happen first, an ordering that under the new
        /// <see cref="AddAction"/> semantics would fire the teardown of a resource that was never opened.
        /// </para>
        /// <para>
        /// If this lifetime terminates concurrently, between the liveness check and the registration, the
        /// resource <i>was</i> acquired — so <paramref name="onTerminate"/> is invoked immediately by
        /// <see cref="AddAction"/>. Acquired, then released: the invariant holds either way.
        /// </para>
        /// <para>
        /// Brackets close in reverse opening order (LIFO), interleaved with plain actions in one sequence.
        /// Neither callback runs under a lock.
        /// </para>
        /// </remarks>
        /// <param name="onOpen">
        /// Acquires the resource. Invoked immediately unless this lifetime is terminated. May be
        /// <c>null</c>, which reduces this to "register <paramref name="onTerminate"/>, but only if the
        /// scope is still open".
        /// </param>
        /// <param name="onTerminate">Releases the resource. Must not be <c>null</c>.</param>
        /// <returns>This lifetime, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="onTerminate"/> is <c>null</c>.</exception>
        public Lifetime AddBracket(Action onOpen, Action onTerminate)
        {
            // Validated before anything is acquired: a bracket that cannot release must not open.
            if (onTerminate == null)
                throw new ArgumentNullException(nameof(onTerminate), $"{nameof(onTerminate)} can't be null");

            // Nothing was acquired in a scope that has already ended, so there is nothing to release.
            if (_isTerminated) return this;

            onOpen?.Invoke();

            return AddAction(onTerminate);
        }

        /// <summary>
        /// Wires <paramref name="definition"/> to terminate when this lifetime terminates, and to
        /// unregister itself from this lifetime if it terminates first.
        /// </summary>
        private void AddDefinition(Definition definition)
        {
            // Captured once, so the registration below and its removal are the same delegate value.
            Action terminateChild = definition.Terminate;

            bool registered;
            lock (_lock)
            {
                registered = !_isTerminated;
                if (registered) (_actions ??= new List<Action>()).Add(terminateChild);
            }

            // Both branches below reach into ANOTHER Lifetime, so neither may run under _lock. Taking the
            // child's lock while holding the parent's is precisely the parent->child edge that used to
            // invert against the child->parent edge of the removal closure below.
            if (!registered)
            {
                definition.Terminate();
                return;
            }

            // If the child terminates first, drop its entry from our list, so a long-lived parent with many
            // short-lived children does not accumulate dead delegates. If the child is already terminated
            // (Intersection can do that), AddAction runs this immediately and the entry is dropped now.
            definition.Lifetime.AddAction(() => Remove(terminateChild));
        }

        /// <summary>
        /// Removes one registration of <paramref name="action"/>. A no-op once terminated: the list has
        /// already been handed off and every entry has already run.
        /// </summary>
        private void Remove(Action action)
        {
            lock (_lock)
            {
                _actions?.Remove(action);
            }
        }

        /// <summary>
        /// Ends this lifetime and runs every registered action in reverse registration order.
        /// Private by design: only the owning <see cref="Definition"/> can end a lifetime.
        /// </summary>
        private void Terminate()
        {
            List<Action> actions;

            lock (_lock)
            {
                // Idempotence, the state flip and the hand-off of the action list all happen inside one
                // critical section. That is the whole of the exactly-once argument: AddAction takes the
                // same lock, so it either appended before this point (and is therefore in `actions`), or it
                // observes _isTerminated and invokes inline. It can never do both, and never neither.
                if (_isTerminated) return;
                _isTerminated = true;
                actions = _actions;
                _actions = null;
            }

            if (actions == null) return;

            // From here on NO lock is held, and this lifetime is already terminated for every observer.
            // An action is free to call back into this lifetime, its parent or its children, and to throw.
            List<Exception> errors = null;
            for (var i = actions.Count - 1; i >= 0; i--)
            {
                try
                {
                    actions[i].Invoke();
                }
                catch (Exception exception)
                {
                    // One bad action must not strand the ones registered before it: keep going, report at
                    // the end. Termination is already committed, so it completes whatever happens here.
                    (errors ??= new List<Exception>()).Add(exception);
                }
            }

            if (errors != null)
                throw new AggregateException(
                    $"One or more termination actions of Lifetime #{_id} threw. The lifetime is fully " +
                    "terminated and every registered action was invoked.", errors);
        }
    }
}
