using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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
    /// <b>Creating scopes.</b> There are exactly two ways: <see cref="DefineNested"/> creates a child that
    /// ends when this lifetime ends, and <see cref="Intersection"/> creates a scope that ends when any of
    /// several lifetimes ends. Either kind can also be ended early by its owner, and then detaches itself.
    /// The root of every tree is <see cref="Eternal"/>; an intersection belongs to the trees of its inputs,
    /// and an intersection of no lifetimes to none.
    /// </para>
    /// <para>
    /// <b>Core invariant.</b> Every action handed to a lifetime runs exactly once. If the lifetime is alive,
    /// the action runs at termination; if the lifetime is already terminated, the action runs immediately,
    /// inside the <see cref="AddAction"/> call. There is no state in which an action is silently dropped.
    /// The same rule covers scopes: a scope created on a terminated lifetime is born terminated.
    /// </para>
    /// <para>
    /// <b>Order.</b> Termination actions run in reverse registration order (LIFO). This is a guarantee, not
    /// an implementation detail: the last resource acquired is the first one released.
    /// </para>
    /// <para>
    /// <b>Failures.</b> A throwing termination action never stops the others. The failures are reported
    /// once every action has run: one failure as itself, two or more as an <see cref="AggregateException"/>
    /// (see <see cref="Definition.Terminate"/>).
    /// </para>
    /// <para>
    /// <b>Thread safety.</b> Every member is safe to call concurrently. No lock is ever held while user code
    /// runs, so a termination action may freely call back into this lifetime, its parent, or its children.
    /// </para>
    /// <para>
    /// 2.0.0 changes the behaviour and the shape of this type in several breaking ways; the package
    /// CHANGELOG lists each one with a migration hint.
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
        /// A definition is created by <see cref="OpenUGD.Lifetime.DefineNested"/> or
        /// <see cref="OpenUGD.Lifetime.Intersection"/>; it has no public constructor. It implements
        /// <see cref="IDisposable"/>, so it composes with <c>using</c> and <c>using var</c>.
        /// </para>
        /// <para>
        /// <b>The implicit conversion to <see cref="OpenUGD.Lifetime"/></b> applies wherever the compiler
        /// converts an expression to that type: an argument, an assignment, a return value. It does
        /// <i>not</i> apply to member access, because C# never applies a user-defined conversion to the
        /// receiver of an instance or extension method. So <c>Load(scope)</c> compiles for
        /// <c>Load(Lifetime)</c>, while <c>scope.AddAction(...)</c>, <c>scope.DefineNested()</c> and
        /// <c>scope.AsCancellationToken()</c> do not: write <c>scope.Lifetime.AddAction(...)</c>.
        /// </para>
        /// <para>
        /// <b>Do not call <see cref="LifetimeExtensions.With{T}"/> on a definition.</b>
        /// <c>scope.With(other)</c> compiles, because a definition is an <see cref="IDisposable"/>, and it
        /// does end <c>scope</c> when <c>other</c> ends — but through a plain action on <c>other</c>, not as a
        /// nested scope, so nothing detaches it when <c>scope</c> ends first: <c>other</c> keeps <c>scope</c>
        /// reachable until <c>other</c> ends. Create the scope where it belongs instead —
        /// <c>other.DefineNested()</c>, or <c>Lifetime.Intersection(parent, other)</c> when it must end with
        /// either of two lifetimes.
        /// </para>
        /// <code>
        /// using var scope = lifetime.DefineNested("load-level");
        /// Load(scope);                       // implicit conversion: Load takes a Lifetime
        /// scope.Lifetime.AddAction(Unload);  // member access needs .Lifetime
        /// </code>
        /// </remarks>
        public class Definition : IDisposable
        {
            internal Definition(string name, int parentId)
            {
                Name = name;
                ParentId = parentId;
                Lifetime = new Lifetime();
            }

            /// <summary>
            /// [used for debugging purposes]
            /// The optional human-readable name given at definition time. Carries no semantics and is not
            /// required to be unique; it exists only to make a lifetime tree readable in a debugger or a
            /// log. May be <c>null</c>.
            /// </summary>
            public string Name { get; }

            /// <summary>
            /// [used for debugging purposes]
            /// The <see cref="OpenUGD.Lifetime.Id"/> of the lifetime this definition was defined on.
            /// Carries no semantics; it exists only to make the lifetime tree readable while debugging.
            /// For a definition produced by <see cref="OpenUGD.Lifetime.Intersection"/> this is <c>0</c>, which
            /// is never the id of a lifetime: an intersection has no single parent.
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
            /// <b>Failures.</b> If actions throw, all of them still run and the lifetime is fully terminated
            /// either way; the failures are reported when the last action has run. <b>Exactly one failure
            /// is rethrown as itself</b>, with its original type and stack trace, so a caller catches the
            /// exception the action threw. <b>Two or more are thrown as one
            /// <see cref="AggregateException"/></b>, in the order they occurred (reverse registration
            /// order). A later call neither re-runs the actions nor re-throws.
            /// </para>
            /// <para>
            /// A nested scope's termination is one entry in its parent's sequence, so its failure reaches the
            /// parent as whatever its own termination threw: a single failure anywhere in a tree arrives
            /// unwrapped, and aggregates nest only where one scope collected two or more. Call
            /// <see cref="AggregateException.Flatten"/> on an aggregate for the leaves.
            /// <i>Changed in 2.0.0</i> — in 1.x the first failure aborted the remaining actions.
            /// </para>
            /// <para>
            /// Concurrency: exactly one caller runs the actions. Other concurrent callers return
            /// <i>immediately</i>, possibly before that caller has finished, and never block. If you need
            /// "clean-up has finished", be the thread that owns the definition.
            /// </para>
            /// </remarks>
            /// <exception cref="Exception">
            /// Exactly one termination action threw: that exception, rethrown with its original stack trace.
            /// </exception>
            /// <exception cref="AggregateException">Two or more termination actions threw.</exception>
            public void Terminate() => Lifetime.Terminate();

            /// <summary>
            /// Same as <see cref="Terminate"/>; lets a definition be used with <c>using</c>.
            /// </summary>
            /// <remarks>
            /// Note that this can therefore throw at the closing brace of a <c>using</c> block; see
            /// <see cref="Terminate"/>.
            /// </remarks>
            /// <exception cref="Exception">
            /// Exactly one termination action threw: that exception, rethrown with its original stack trace.
            /// </exception>
            /// <exception cref="AggregateException">Two or more termination actions threw.</exception>
            public void Dispose() => Lifetime.Terminate();

            /// <summary>
            /// Converts a definition to the lifetime it owns, so a definition can be passed wherever a
            /// <see cref="OpenUGD.Lifetime"/> is expected. Null-safe.
            /// </summary>
            /// <remarks>
            /// The conversion is one-way, so passing a definition to a method that takes a
            /// <see cref="OpenUGD.Lifetime"/> hands over the right to observe and register, never the right
            /// to terminate. It does not apply to member access — see the remarks on
            /// <see cref="Definition"/>.
            /// </remarks>
            /// <param name="definition">The definition to convert; may be <c>null</c>.</param>
            /// <returns>
            /// The owned <see cref="OpenUGD.Lifetime"/>, or <c>null</c> if <paramref name="definition"/>
            /// is <c>null</c>.
            /// </returns>
            public static implicit operator Lifetime(Definition definition) => definition?.Lifetime;
        }

        private static int _instances;

        // The ParentId of an intersection. Ids start at 1 (see the constructor), so no lifetime has this one.
        private const int NoParentId = 0;

        /// <summary>
        /// A lifetime that never terminates. Use it as the root of a lifetime tree and as the parent of
        /// scopes that live as long as the process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Anything registered directly on <see cref="Eternal"/> is kept alive forever and never runs, so
        /// register on a nested definition instead whenever the thing being registered can go away earlier.
        /// </para>
        /// <para>
        /// <b>Process-wide.</b> <see cref="Eternal"/> is a static field, so it lives as long as the
        /// application domain. In the Unity editor with domain reload disabled (Enter Play Mode Options) it
        /// survives from one play session to the next, together with every scope still nested in it. Give
        /// the application its own root definition, nested in <see cref="Eternal"/> and terminated when the
        /// application or the play session ends, and nest everything else in that root.
        /// </para>
        /// </remarks>
        public static readonly Lifetime Eternal = new Lifetime();

        // The size of the first entry array, and the size up to which an array is never shrunk (see Compact).
        private const int InitialCapacity = 4;
        private const int ShrinkThreshold = 64;

        // The one and only lock of this type. It is taken exclusively around reads and writes of the fields
        // below (and of Child.Index) - never around a call to user code, and never around a call into another
        // Lifetime. Every lock block in this file does only field and array work on this instance (Append,
        // Detach and Compact included); verify by eye.
        private readonly object _lock = new object();
        private readonly int _id;

        // The registration sequence, oldest first. Each slot holds an Action, a Child (a nested definition),
        // or null: a child that terminated first and detached. Detaching empties its slot by index, which is
        // O(1); Compact squeezes the empty slots out once they are more than half of the used range, so the
        // cost is amortised O(1) per detach and terminating n children one by one is O(n).
        // Null array means "none": nothing registered yet (allocation is deferred), every entry detached and
        // compacted away, or handed off to Terminate. Only ever touched under _lock.
        private object[] _entries;
        private int _count;      // used slots, empty ones included; the next registration goes to _count
        private int _emptySlots; // null slots below _count

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
        /// on it; that is fine by design, because <see cref="AddAction"/>, <see cref="AddBracket"/> and
        /// <see cref="DefineNested"/> are correct whichever way the race falls. Prefer calling them over
        /// testing this first.
        /// </remarks>
        public bool IsTerminated => _isTerminated;

        /// <summary>
        /// Creates a scope nested in this lifetime: when this lifetime terminates, the new one terminates
        /// too. This is the one way to create a child scope.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Terminating the returned definition first detaches it from this lifetime, so a long-lived parent
        /// with many short-lived children does not accumulate dead entries. Detaching costs amortised O(1)
        /// however many siblings the child has, so ending n children one by one is linear in n. The child's
        /// termination is registered in this lifetime's single LIFO sequence, interleaved with its actions: a
        /// child created after an action is terminated before that action runs.
        /// </para>
        /// <para>
        /// <b>If this lifetime is already terminated, the returned definition is already terminated</b>,
        /// and whatever is then registered on it runs immediately, as for any terminated lifetime. A live
        /// child of a dead parent is never produced. <i>Changed in 2.0.0</i> — this used to throw
        /// <see cref="InvalidOperationException"/>, unlike registering anything else on a terminated
        /// lifetime; test <see cref="Definition.IsTerminated"/> on the result if you need to know.
        /// </para>
        /// </remarks>
        /// <param name="name">
        /// [used for debugging purposes] Optional name for the new definition; see <see cref="Definition.Name"/>.
        /// </param>
        /// <returns>The new definition. The caller owns it and is responsible for terminating it.</returns>
        public Definition DefineNested(string name = null)
        {
            var definition = new Definition(name, _id);
            AddDefinition(definition);
            return definition;
        }

        /// <summary>
        /// Creates a scope that terminates as soon as <i>any</i> of <paramref name="lifetimes"/> terminates
        /// — the intersection of all of them. Use it for something that is valid only while several
        /// independent scopes are all alive, which a parent-child tree cannot express.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If one of <paramref name="lifetimes"/> is already terminated, the returned definition is already
        /// terminated. Terminating the returned definition first detaches it from every one of
        /// <paramref name="lifetimes"/>.
        /// </para>
        /// <para>
        /// The intersection is attached to <paramref name="lifetimes"/> and to nothing else — in particular not
        /// to <see cref="Eternal"/>. So it is reachable for exactly as long as one of them is, or its owner
        /// holds it: abandoned together with the scopes it intersects, it is collected with them. An
        /// intersection of no lifetimes is attached to nothing at all.
        /// <i>Changed in 2.0.0</i> — every intersection used to be nested in <see cref="Eternal"/> as well, so
        /// one that was never terminated stayed reachable for the life of the process, together with the
        /// lifetimes it intersected.
        /// </para>
        /// </remarks>
        /// <param name="lifetimes">
        /// The lifetimes to intersect. An empty array yields the vacuous intersection: a definition that
        /// nothing will terminate on its own, but that its owner can still terminate. The same lifetime may
        /// appear more than once.
        /// </param>
        /// <returns>
        /// The new definition. The caller owns it: until it is terminated it stays attached to every
        /// lifetime in <paramref name="lifetimes"/>.
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
            // half-built definition attached to some of the lifetimes.
            foreach (var lifetime in lifetimes)
            {
                if (lifetime == null)
                    throw new ArgumentNullException(nameof(lifetimes),
                        $"{nameof(lifetimes)} can't contain null on define an intersection");
            }

            // Attached to its inputs only. Each AddDefinition is the same race-free wiring DefineNested uses:
            // a dead input terminates the definition on the spot, and every later input then sees it
            // terminated and detaches at once (see AddDefinition).
            var definition = new Definition(null, NoParentId);
            foreach (var lifetime in lifetimes)
            {
                lifetime.AddDefinition(definition);
            }

            return definition;
        }

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
                    Append(action);
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
        /// unregister itself from this lifetime if it terminates first. If this lifetime is already
        /// terminated, terminates <paramref name="definition"/> now.
        /// </summary>
        private void AddDefinition(Definition definition)
        {
            Child child = null;
            lock (_lock)
            {
                if (!_isTerminated)
                {
                    child = new Child(this, definition);
                    child.Index = Append(child);
                }
            }

            // Both branches below reach into ANOTHER Lifetime, so neither may run under _lock. Taking the
            // child's lock while holding the parent's is precisely the parent->child edge that used to
            // invert against the child->parent edge of Detach below.
            if (child == null)
            {
                definition.Terminate();
                return;
            }

            // If the child terminates first, empty its slot, so a long-lived parent with many short-lived
            // children does not accumulate dead entries. If the child is already terminated (Intersection
            // can do that), AddAction runs this immediately and the slot is emptied now.
            definition.Lifetime.AddAction(child.Detach);
        }

        /// <summary>
        /// Appends <paramref name="entry"/> to the registration sequence and returns its slot. Caller holds
        /// <see cref="_lock"/> and has checked that this lifetime is not terminated.
        /// </summary>
        private int Append(object entry)
        {
            // Lazily allocated: a lifetime that never receives a registration never allocates an array.
            if (_entries == null) _entries = new object[InitialCapacity];
            else if (_count == _entries.Length) Array.Resize(ref _entries, _count * 2);

            _entries[_count] = entry;
            return _count++;
        }

        /// <summary>
        /// Empties the slot of <paramref name="child"/>, which terminated before this lifetime did. O(1),
        /// amortised. A no-op once this lifetime is terminated: the entries have been handed off, and the
        /// child has run or is about to.
        /// </summary>
        private void Detach(Child child)
        {
            lock (_lock)
            {
                var index = child.Index;
                if (_isTerminated || index < 0) return;

                child.Index = -1;
                _entries[index] = null;
                _emptySlots++;

                // Short-lived scopes usually end newest first, so most detaches just shorten the used range.
                while (_count > 0 && _entries[_count - 1] == null)
                {
                    _count--;
                    _emptySlots--;
                }

                // More than half of the used range empty, or a large array mostly unused after a burst.
                if (_emptySlots * 2 > _count || (_entries.Length > ShrinkThreshold && _count * 4 < _entries.Length))
                    Compact();
            }
        }

        /// <summary>
        /// Squeezes the empty slots out of the registration sequence, preserving its order, and rewrites the
        /// slot of every child that moves. Shrinks the array when it is mostly unused. Caller holds
        /// <see cref="_lock"/>. O(used range), paid for by the detaches that emptied at least half of it.
        /// </summary>
        private void Compact()
        {
            var source = _entries;
            var live = _count - _emptySlots;
            if (live == 0)
            {
                _entries = null;
                _count = 0;
                _emptySlots = 0;
                return;
            }

            var target = source.Length > ShrinkThreshold && live * 4 < source.Length
                ? new object[Math.Max(InitialCapacity, live * 2)]
                : source;

            var write = 0;
            for (var read = 0; read < _count; read++)
            {
                var entry = source[read];
                if (entry == null) continue;
                if (entry is Child child) child.Index = write;
                target[write++] = entry;
            }

            if (target == source) Array.Clear(source, write, _count - write);

            _entries = target;
            _count = write;
            _emptySlots = 0;
        }

        /// <summary>
        /// The registration of one nested definition in one parent: an entry in the parent's sequence that
        /// knows its own slot, so the child can leave in O(1). A definition attached to several parents (an
        /// intersection) has one of these per parent.
        /// </summary>
        private sealed class Child
        {
            private readonly Lifetime _parent;

            // The slot in _parent._entries, or -1 once detached or before it is appended. Read and written
            // only under _parent._lock.
            internal int Index = -1;

            internal Child(Lifetime parent, Definition definition)
            {
                _parent = parent;
                Definition = definition;
            }

            internal Definition Definition { get; }

            // Registered on the child's own lifetime: runs when the child terminates.
            internal void Detach() => _parent.Detach(this);
        }

        /// <summary>
        /// Ends this lifetime and runs every registered action in reverse registration order.
        /// Private by design: only the owning <see cref="Definition"/> can end a lifetime.
        /// </summary>
        private void Terminate()
        {
            object[] entries;
            int count;

            lock (_lock)
            {
                // Idempotence, the state flip and the hand-off of the entries all happen inside one critical
                // section. That is the whole of the exactly-once argument: AddAction and AddDefinition take
                // the same lock, so a registration either landed before this point (and is therefore in
                // `entries`), or it observes _isTerminated and runs inline. It can never do both, and never
                // neither. Detach observes _isTerminated too, so nothing touches `entries` after the hand-off.
                if (_isTerminated) return;
                _isTerminated = true;
                entries = _entries;
                count = _count;
                _entries = null;
                _count = 0;
                _emptySlots = 0;
            }

            if (entries == null) return;

            // From here on NO lock is held, and this lifetime is already terminated for every observer.
            // An action is free to call back into this lifetime, its parent or its children, and to throw.
            Exception failure = null;
            List<Exception> failures = null;
            for (var i = count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry == null) continue; // a child that detached first

                try
                {
                    if (entry is Action action) action();
                    else ((Child)entry).Definition.Terminate();
                }
                catch (Exception exception)
                {
                    // One bad action must not strand the ones registered before it: keep going, report at
                    // the end. Termination is already committed, so it completes whatever happens here.
                    if (failure == null) failure = exception;
                    else (failures ??= new List<Exception> { failure }).Add(exception);
                }
            }

            if (failure == null) return;

            // Exactly one failure: rethrow that exception itself, keeping its type and its original stack
            // trace, so callers catch what the action threw instead of unwrapping a one-element aggregate.
            if (failures == null) ExceptionDispatchInfo.Capture(failure).Throw();

            throw new AggregateException(
                $"{failures.Count} termination actions of Lifetime #{_id} threw. The lifetime is fully " +
                "terminated and every registered action was invoked.", failures);
        }
    }
}
