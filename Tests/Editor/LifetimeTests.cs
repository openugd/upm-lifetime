using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

// A few assertion messages start with a tag for the rule they check:
//   R1   every action handed to a lifetime runs exactly once: at termination, or immediately if the
//        lifetime has already ended
//   R4   a throwing termination action does not stop the others; one failure is rethrown as itself,
//        two or more as one AggregateException
//   R8   a Definition converts implicitly to its Lifetime, and the conversion is null-safe
//   R12  the shape of the public API: AddAction and AddBracket return the Lifetime, and Id, Name and
//        ParentId are debugging aids that are still part of it

namespace OpenUGD.Tests
{
    /// <summary>
    /// Behavioural suite for OpenUGD.Lifetime 2.0.0 — deterministic, single threaded, fast.
    /// Concurrency and stress live in LifetimeConcurrencyTests so that this fixture stays instant to run.
    ///
    /// State hygiene: Lifetime.Eternal is a process-wide static that never terminates, so anything
    /// registered on it directly would live for the whole test run. Every test therefore hangs its
    /// lifetimes off a per-test root definition that TearDown terminates. Intersections are not nested in
    /// that root (an intersection is attached to its inputs only), so they are tracked and terminated too.
    /// </summary>
    [TestFixture]
    public class LifetimeTests
    {
        private Lifetime.Definition _root;
        private List<Lifetime.Definition> _tracked;

        [SetUp]
        public void SetUp()
        {
            _root = Lifetime.Eternal.DefineNested("test-root");
            _tracked = new List<Lifetime.Definition>();
        }

        [TearDown]
        public void TearDown()
        {
            // Terminate the tracked definitions first, then the per-test root. Some tests register
            // throwing actions on purpose; swallowing here keeps a clean-up failure from masking the real
            // assertion failure.
            foreach (var definition in _tracked)
            {
                try
                {
                    definition.Terminate();
                }
                catch (Exception)
                {
                    // ignored, see above
                }
            }

            _tracked.Clear();

            try
            {
                _root.Terminate();
            }
            catch (Exception)
            {
                // ignored, see above
            }

            _root = null;
        }

        /// <summary>Registers a definition outside the per-test root, such as an intersection, for termination in TearDown.</summary>
        private Lifetime.Definition Track(Lifetime.Definition definition)
        {
            _tracked.Add(definition);
            return definition;
        }

        /// <summary>A fresh, live definition owned by this test.</summary>
        private Lifetime.Definition NewDefinition(string name = null) => _root.Lifetime.DefineNested(name);

        /// <summary>A fresh, already terminated definition owned by this test.</summary>
        private Lifetime.Definition NewTerminatedDefinition(string name = null)
        {
            var definition = _root.Lifetime.DefineNested(name);
            definition.Terminate();
            return definition;
        }

        private sealed class MarkerException : Exception
        {
            public MarkerException(string message) : base(message)
            {
            }
        }

        private sealed class CountingDisposable : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class ThrowingDisposable : IDisposable
        {
            public readonly MarkerException Error = new MarkerException("dispose");
            public void Dispose() => throw Error;
        }

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _onDispose;
            public ActionDisposable(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }

        /// <summary>
        /// Throws <paramref name="exception"/> from a frame of its own, so a test can check that the frame
        /// survives a rethrow.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFromNamedFrame(Exception exception) => throw exception;

        // ------------------------------------------------------------------------------------------
        // Eternal, Name / ParentId / Id (debugging aids, but part of the public API)
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Eternal_IsNeverTerminated()
        {
            Assert.IsNotNull(Lifetime.Eternal);
            Assert.IsFalse(Lifetime.Eternal.IsTerminated, "Eternal must never be terminated.");
            Assert.IsTrue(Lifetime.Eternal.IsAlive());
        }

        [Test]
        public void Eternal_IsUnaffectedByAnythingDoneToNestedDefinitions()
        {
            // Guard for the whole suite: a test that terminated Eternal would poison every later test.
            var definition = NewDefinition("eternal-guard");
            definition.Terminate();
            _root.Terminate();

            Assert.IsFalse(Lifetime.Eternal.IsTerminated);
            Assert.IsTrue(Lifetime.Eternal.IsAlive());
        }

        [Test]
        public void NewDefinition_IsAliveAndExposesOneStableLifetimeInstance()
        {
            var definition = NewDefinition("stable-lifetime");

            Assert.IsNotNull(definition.Lifetime);
            Assert.AreSame(definition.Lifetime, definition.Lifetime, "Definition.Lifetime must be stable.");
            Assert.IsFalse(definition.IsTerminated);
            Assert.IsFalse(definition.Lifetime.IsTerminated);
        }

        [Test]
        public void DefinitionName_IsTheSuppliedNameAndIsNullWhenOmitted()
        {
            var named = NewDefinition("my-debug-name");
            var unnamed = NewDefinition();

            Assert.AreEqual("my-debug-name", named.Name);
            Assert.IsNull(unnamed.Name, "An omitted name must stay null rather than being invented.");
        }

        [Test]
        public void DefinitionParentId_EqualsTheIdOfTheLifetimeItWasDefinedOn()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");

            Assert.AreEqual(parent.Lifetime.Id, child.ParentId);
        }

        [Test]
        public void LifetimeId_IsUniquePerInstanceAndStableAcrossTermination()
        {
            var first = NewDefinition("a");
            var second = NewDefinition("b");
            var third = first.Lifetime.DefineNested("c");

            var ids = new[] { first.Lifetime.Id, second.Lifetime.Id, third.Lifetime.Id, Lifetime.Eternal.Id };
            Assert.AreEqual(ids.Length, ids.Distinct().Count(), "Every Lifetime must carry a distinct debug id.");

            var before = first.Lifetime.Id;
            first.Terminate();
            Assert.AreEqual(before, first.Lifetime.Id, "Termination must not renumber a lifetime.");
        }

        // ------------------------------------------------------------------------------------------
        // DefineNested
        // ------------------------------------------------------------------------------------------

        [Test]
        public void DefineNested_OnATerminatedLifetime_ReturnsABornTerminatedDefinition()
        {
            // The same dead-scope rule as AddAction, With and AsCancellationToken - no throw, and
            // never a live child of a dead parent. 1.x threw InvalidOperationException here.
            var dead = NewTerminatedDefinition("dead-parent");

            Lifetime.Definition child = null;
            Assert.DoesNotThrow(() => child = dead.Lifetime.DefineNested("child"));

            Assert.IsTrue(child.IsTerminated, "A child of a dead parent is born terminated.");
            Assert.AreEqual("child", child.Name);
            Assert.AreEqual(dead.Lifetime.Id, child.ParentId);

            var calls = 0;
            child.Lifetime.AddAction(() => calls++);
            Assert.AreEqual(1, calls, "R1: registering on the born-terminated child runs immediately.");
            Assert.IsTrue(child.Lifetime.AsCancellationToken().IsCancellationRequested);
            Assert.DoesNotThrow(() => child.Terminate(), "Terminating it again is an idempotent no-op.");
        }

        [Test]
        public void DefineNested_OnALifetimeTerminatedByItsParent_ReturnsABornTerminatedDefinition()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            parent.Terminate();

            var grandChild = child.Lifetime.DefineNested("grand-child");

            Assert.IsTrue(grandChild.IsTerminated);
        }

        [Test]
        public void DefineNested_FromInsideATerminationAction_ReturnsABornTerminatedChild()
        {
            // IsTerminated is already true while actions run, so DefineNested takes its terminated path: a
            // live child of a dying parent is never produced.
            var parent = NewDefinition("define-during-termination");
            Lifetime.Definition lateChild = null;
            var lateChildRuns = 0;

            parent.Lifetime.AddAction(() =>
            {
                lateChild = parent.Lifetime.DefineNested("late-child");
                lateChild.Lifetime.AddAction(() => lateChildRuns++);
            });

            parent.Terminate();

            Assert.IsNotNull(lateChild);
            Assert.IsTrue(lateChild.IsTerminated);
            Assert.AreEqual(1, lateChildRuns);
        }

        [Test]
        public void DefineNested_ChildTerminationTakesItsPlaceInTheParentsLifoSequence()
        {
            // A child is one entry in the parent's single LIFO sequence, interleaved with plain actions: a
            // child created after an action is terminated before that action runs.
            var parent = NewDefinition("interleaved");
            var log = new List<string>();

            parent.Lifetime.AddAction(() => log.Add("action-1"));
            var first = parent.Lifetime.DefineNested("child-1");
            first.Lifetime.AddAction(() => log.Add("child-1"));
            parent.Lifetime.AddAction(() => log.Add("action-2"));
            var second = parent.Lifetime.DefineNested("child-2");
            second.Lifetime.AddAction(() => log.Add("child-2"));

            parent.Terminate();

            CollectionAssert.AreEqual(new[] { "child-2", "action-2", "child-1", "action-1" }, log);
        }

        // ------------------------------------------------------------------------------------------
        // Intersection
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Intersection_IsAliveWhileAllSourceLifetimesAreAlive()
        {
            var first = NewDefinition("L1");
            var second = NewDefinition("L2");

            var intersection = Track(Lifetime.Intersection(first.Lifetime, second.Lifetime));

            Assert.IsFalse(intersection.IsTerminated);
        }

        [Test]
        public void Intersection_TerminatesAsSoonAsAnySourceTerminates_AndLeavesTheOtherSourcesAlone()
        {
            var first = NewDefinition("L1");
            var second = NewDefinition("L2");
            var third = NewDefinition("L3");
            var intersection = Track(Lifetime.Intersection(first.Lifetime, second.Lifetime, third.Lifetime));
            var calls = 0;
            intersection.Lifetime.AddAction(() => calls++);

            second.Terminate();

            Assert.IsTrue(intersection.IsTerminated, "An intersection dies with the first source lifetime.");
            Assert.IsFalse(first.IsTerminated, "The surviving source lifetimes are unaffected.");
            Assert.IsFalse(third.IsTerminated);

            first.Terminate();
            third.Terminate();
            Assert.AreEqual(1, calls, "An intersection must fire its actions exactly once.");
        }

        [Test]
        public void Intersection_WithAnAlreadyTerminatedSource_IsBornTerminatedInEitherArgumentPosition()
        {
            var dead = NewTerminatedDefinition("dead");
            var alive = NewDefinition("alive");

            var deadFirst = Track(Lifetime.Intersection(dead.Lifetime, alive.Lifetime));
            var deadLast = Track(Lifetime.Intersection(alive.Lifetime, dead.Lifetime));

            Assert.IsTrue(deadFirst.IsTerminated, "A dead source passed first must terminate the intersection.");
            Assert.IsTrue(deadLast.IsTerminated, "A dead source passed last must terminate it too.");

            var calls = 0;
            deadFirst.Lifetime.AddAction(() => calls++);
            deadLast.Lifetime.AddAction(() => calls++);
            Assert.AreEqual(2, calls, "R1: registering on a dead intersection invokes immediately.");

            alive.Terminate();
            Assert.AreEqual(2, calls, "R1: and never a second time.");
            Assert.IsFalse(_root.IsTerminated, "An intersection must not terminate lifetimes it merely observes.");
        }

        [Test]
        public void Intersection_TerminatedBeforeItsSources_DetachesFromEveryOneOfThem()
        {
            var first = NewDefinition("first");
            var second = NewDefinition("second");
            var intersection = Track(Lifetime.Intersection(first.Lifetime, second.Lifetime));
            var calls = 0;
            intersection.Lifetime.AddAction(() => calls++);

            intersection.Terminate();
            Assert.AreEqual(1, calls);

            Assert.DoesNotThrow(() => first.Terminate());
            Assert.DoesNotThrow(() => second.Terminate());
            Assert.AreEqual(1, calls, "A later source termination must not re-run a dead intersection.");
        }

        [Test]
        public void Intersection_WithTheSameLifetimeSuppliedTwice_TerminatesOnceWithoutThrowing()
        {
            // Duplicates are no longer rejected, so intersecting one lifetime with itself must be
            // harmless — Terminate is idempotent, so the actions still run exactly once.
            var lifetime = NewDefinition("repeated");
            Lifetime.Definition intersection = null;

            Assert.DoesNotThrow(() =>
                intersection = Track(Lifetime.Intersection(lifetime.Lifetime, lifetime.Lifetime)));

            var calls = 0;
            intersection.Lifetime.AddAction(() => calls++);

            lifetime.Terminate();

            Assert.AreEqual(1, calls);
            Assert.IsTrue(intersection.IsTerminated);
        }

        [Test]
        public void Intersection_WithNoLifetimes_IsAliveAndMustBeTerminatedByItsOwner()
        {
            var intersection = Track(Lifetime.Intersection());

            Assert.IsFalse(intersection.IsTerminated);

            var calls = 0;
            intersection.Lifetime.AddAction(() => calls++);
            intersection.Terminate();

            Assert.AreEqual(1, calls);
        }

        [Test]
        public void Intersection_HasNoParentId()
        {
            var first = NewDefinition("first");
            var second = NewDefinition("second");

            var intersection = Track(Lifetime.Intersection(first.Lifetime, second.Lifetime));

            Assert.AreEqual(0, intersection.ParentId,
                "An intersection has no single parent; 0 is never the id of a lifetime.");
            Assert.AreNotEqual(0, Lifetime.Eternal.Id);
        }

        [Test]
        public void Intersection_OfNoLifetimes_IsCollectedOnceItsOwnerLetsGoOfIt()
        {
            // An intersection used to be nested in Eternal as well as in its inputs, so one that was never
            // terminated stayed reachable for the life of the process. With no inputs, its owner is the only
            // thing that may hold it.
            var weak = OnAThreadOfItsOwn(() => new WeakReference(Lifetime.Intersection()));

            CollectGarbage();

            Assert.IsFalse(weak.IsAlive, "An abandoned intersection must not be kept alive by Eternal.");
        }

        [Test]
        public void Intersection_OfAbandonedLifetimes_IsCollectedTogetherWithThem()
        {
            // The intersection's detach actions refer to its inputs, so whatever kept an abandoned intersection
            // alive kept the scopes it intersected alive too, and their whole tree with them.
            var weak = OnAThreadOfItsOwn(() =>
            {
                var root = Lifetime.Intersection();
                var first = root.Lifetime.DefineNested("first");
                var second = root.Lifetime.DefineNested("second");
                var intersection = Lifetime.Intersection(first.Lifetime, second.Lifetime);
                intersection.Lifetime.AddAction(() => { });
                return new[]
                {
                    new WeakReference(intersection), new WeakReference(first), new WeakReference(second),
                    new WeakReference(root)
                };
            });

            CollectGarbage();

            Assert.IsFalse(weak[0].IsAlive, "An abandoned intersection must be collected with its inputs.");
            Assert.IsTrue(weak.All(reference => !reference.IsAlive),
                "Nothing outside the abandoned tree may keep its scopes alive.");
        }

        [Test]
        public void Intersection_WithANullArrayOrANullElement_ThrowsArgumentNullException()
        {
            // 1.x threw NullReferenceException part-way through wiring, leaving a definition attached to
            // Eternal forever.
            var alive = NewDefinition("alive");

            Assert.Throws<ArgumentNullException>(() => Lifetime.Intersection(null));
            Assert.Throws<ArgumentNullException>(() => Lifetime.Intersection(alive.Lifetime, null));
        }

        // ------------------------------------------------------------------------------------------
        // Parent -> child cascade
        // ------------------------------------------------------------------------------------------

        [Test]
        public void TerminatingParent_CascadesThroughTheWholeDescendantChain()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            var grandChild = child.Lifetime.DefineNested("grand-child");
            var greatGrandChild = grandChild.Lifetime.DefineNested("great-grand-child");
            var childCalls = 0;
            var grandChildCalls = 0;
            child.Lifetime.AddAction(() => childCalls++);
            grandChild.Lifetime.AddAction(() => grandChildCalls++);

            parent.Terminate();

            Assert.IsTrue(child.IsTerminated);
            Assert.IsTrue(child.Lifetime.IsTerminated);
            Assert.IsTrue(grandChild.IsTerminated);
            Assert.IsTrue(greatGrandChild.IsTerminated);
            Assert.AreEqual(1, childCalls);
            Assert.AreEqual(1, grandChildCalls);
        }

        [Test]
        public void TerminatingParent_TerminatesAllSiblingChildren()
        {
            var parent = NewDefinition("parent");
            var children = Enumerable.Range(0, 5)
                .Select(i => parent.Lifetime.DefineNested("child-" + i))
                .ToArray();

            parent.Terminate();

            CollectionAssert.AreEqual(
                Enumerable.Repeat(true, children.Length).ToArray(),
                children.Select(c => c.IsTerminated).ToArray());
        }

        [Test]
        public void TerminatingChild_DoesNotTerminateItsParentAndLeavesItUsable()
        {
            var parent = NewDefinition("parent");
            var first = parent.Lifetime.DefineNested("first");

            first.Terminate();

            Assert.IsTrue(first.IsTerminated);
            Assert.IsFalse(parent.IsTerminated, "Termination propagates downwards only.");
            Assert.IsFalse(parent.Lifetime.IsTerminated);

            var second = parent.Lifetime.DefineNested("second");
            Assert.IsFalse(second.IsTerminated);
            Assert.AreEqual(parent.Lifetime.Id, second.ParentId);
        }

        // ------------------------------------------------------------------------------------------
        // Terminate / Dispose / using / implicit conversion
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Terminate_IsIdempotentAndInterchangeableWithDispose()
        {
            var definition = NewDefinition("idempotent");
            var calls = 0;
            definition.Lifetime.AddAction(() => calls++);

            definition.Terminate();
            definition.Dispose();
            definition.Terminate();

            Assert.AreEqual(1, calls, "Repeated Terminate/Dispose must not re-run actions.");
            Assert.IsTrue(definition.IsTerminated);
        }

        [Test]
        public void Dispose_ThroughTheIDisposableInterface_TerminatesTheLifetime()
        {
            // A single public Dispose() must still satisfy IDisposable after the redundant explicit
            // implementation was deleted.
            var definition = NewDefinition("idisposable");
            var calls = 0;
            definition.Lifetime.AddAction(() => calls++);

            IDisposable asDisposable = definition;
            asDisposable.Dispose();
            asDisposable.Dispose();

            Assert.IsTrue(definition.IsTerminated);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void UsingBlock_TerminatesTheDefinitionOnScopeExit()
        {
            var calls = 0;
            Lifetime.Definition captured;

            using (var definition = NewDefinition("using-block"))
            {
                captured = definition;
                definition.Lifetime.AddAction(() => calls++);
                Assert.IsFalse(definition.IsTerminated, "The scope is still open inside the using block.");
                Assert.AreEqual(0, calls);
            }

            Assert.IsTrue(captured.IsTerminated);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void UsingBlock_TerminatesTheDefinitionEvenWhenTheBodyThrows()
        {
            var calls = 0;
            Lifetime.Definition captured = null;

            Assert.Throws<InvalidOperationException>(() =>
            {
                using (var definition = NewDefinition("using-throws"))
                {
                    captured = definition;
                    definition.Lifetime.AddAction(() => calls++);
                    throw new InvalidOperationException("boom");
                }
            });

            Assert.IsNotNull(captured);
            Assert.IsTrue(captured.IsTerminated);
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void ImplicitConversion_FromDefinitionYieldsItsLifetimeAndIsNullSafe()
        {
            var definition = NewDefinition("implicit");
            Lifetime lifetime = definition;
            Assert.AreSame(definition.Lifetime, lifetime);

            Lifetime.Definition none = null;
            Lifetime converted = null;
            Assert.DoesNotThrow(() => converted = none);
            Assert.IsNull(converted, "R8: the conversion must be null-safe.");
        }

        /// <summary>Stands in for any API that takes a <see cref="Lifetime"/> parameter.</summary>
        private static Lifetime.Definition DefineChildOf(Lifetime parent, string name) => parent.DefineNested(name);

        [Test]
        public void ImplicitConversion_LetsADefinitionBePassedWhereALifetimeIsExpected()
        {
            var definition = NewDefinition("implicit-arg");
            var calls = 0;

            var child = DefineChildOf(definition, "child-of-definition");
            child.Lifetime.AddAction(() => calls++);
            var disposable = new CountingDisposable();
            disposable.With(definition);
            Assert.IsTrue(((Lifetime)definition).IsAlive());
            Assert.AreEqual(definition.Lifetime.Id, child.ParentId);

            definition.Terminate();

            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, disposable.DisposeCount);
            Assert.IsTrue(child.IsTerminated);
        }

        [Test]
        public void IsTerminated_OnDefinitionMirrorsIsTerminatedOnItsLifetime()
        {
            var definition = NewDefinition("mirror");
            Assert.AreEqual(definition.Lifetime.IsTerminated, definition.IsTerminated);
            Assert.IsFalse(definition.IsTerminated);

            definition.Terminate();

            Assert.AreEqual(definition.Lifetime.IsTerminated, definition.IsTerminated);
            Assert.IsTrue(definition.IsTerminated);
        }

        // ------------------------------------------------------------------------------------------
        // AddAction
        // ------------------------------------------------------------------------------------------

        [Test]
        public void AddAction_InvokesTheActionWhenTheLifetimeTerminates()
        {
            var definition = NewDefinition("add-action");
            var called = false;
            definition.Lifetime.AddAction(() => called = true);

            Assert.IsFalse(called, "The action must not run before termination.");
            definition.Terminate();

            Assert.IsTrue(called);
        }

        [Test]
        public void AddAction_RunsActionsInReverseRegistrationOrderAndReturnsTheLifetimeForChaining()
        {
            // Reverse registration order, and AddAction returns the Lifetime so that calls chain.
            var definition = NewDefinition("lifo");
            var order = new List<string>();

            var returned = definition.Lifetime
                .AddAction(() => order.Add("first"))
                .AddAction(() => order.Add("second"))
                .AddAction(() => order.Add("third"));

            Assert.AreSame(definition.Lifetime, returned);
            definition.Terminate();
            CollectionAssert.AreEqual(new[] { "third", "second", "first" }, order);
        }

        [Test]
        public void AddAction_OnTerminatedLifetime_InvokesTheActionImmediatelyAndExactlyOnce()
        {
            // Every action handed to a Lifetime runs exactly once, no matter when it was handed over.
            // 1.x silently dropped the action.
            var definition = NewTerminatedDefinition("dead");
            var calls = 0;
            var observedBefore = -1;

            definition.Lifetime.AddAction(() =>
            {
                observedBefore = calls;
                calls++;
            });

            Assert.AreEqual(1, calls, "The action must have run before AddAction returned.");
            Assert.AreEqual(0, observedBefore);

            definition.Terminate(); // idempotent no-op; must not replay the action
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void AddAction_OnTerminatedLifetime_StillReturnsTheLifetimeAndRunsInCallOrder()
        {
            var definition = NewTerminatedDefinition("dead-chain");
            var order = new List<string>();

            var returned = definition.Lifetime
                .AddAction(() => order.Add("first"))
                .AddAction(() => order.Add("second"));

            Assert.AreSame(definition.Lifetime, returned);
            CollectionAssert.AreEqual(new[] { "first", "second" }, order,
                "Immediately invoked actions run as they are handed over, so in call order.");
        }

        [Test]
        public void AddAction_OnALifetimeTerminatedByItsParent_InvokesTheActionImmediately()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            parent.Terminate();
            var calls = 0;

            child.Lifetime.AddAction(() => calls++);

            Assert.AreEqual(1, calls);
        }

        [Test]
        public void AddAction_OnTerminatedLifetime_PropagatesAThrowFromTheImmediateInvocation()
        {
            // The immediate invocation is a real invocation, so its failure must reach the call site
            // rather than being swallowed — swallowing is the very class of bug the immediate invocation
            // removes.
            var definition = NewTerminatedDefinition("dead-throwing");
            var boom = new MarkerException("immediate");

            var thrown = Assert.Catch<Exception>(() => definition.Lifetime.AddAction(() => throw boom));

            Assert.AreSame(boom, thrown, "The original exception must propagate unwrapped.");
        }

        [Test]
        public void AddAction_WithTheSameDelegateTwice_RegistersTwiceAndInvokesItTwice()
        {
            // Duplicates are permitted and run once per registration (multicast semantics).
            // 1.x threw ArgumentException for the second registration.
            var definition = NewDefinition("duplicates");
            var calls = 0;
            Action action = () => calls++;

            Assert.DoesNotThrow(() =>
            {
                definition.Lifetime.AddAction(action);
                definition.Lifetime.AddAction(action);
                definition.Lifetime.AddAction(action);
            });

            definition.Terminate();
            Assert.AreEqual(3, calls, "One invocation per registration.");
        }

        [Test]
        public void AddAction_WithDuplicatesInterleaved_KeepsStrictReverseRegistrationOrder()
        {
            var definition = NewDefinition("duplicate-order");
            var order = new List<string>();
            Action duplicate = () => order.Add("dup");

            definition.Lifetime.AddAction(duplicate);
            definition.Lifetime.AddAction(() => order.Add("middle"));
            definition.Lifetime.AddAction(duplicate);

            definition.Terminate();

            CollectionAssert.AreEqual(new[] { "dup", "middle", "dup" }, order);
        }

        [Test]
        public void AddAction_WithTheSameDelegateTwiceOnATerminatedLifetime_InvokesItTwice()
        {
            var definition = NewTerminatedDefinition("duplicates-dead");
            var calls = 0;
            Action action = () => calls++;

            definition.Lifetime.AddAction(action);
            definition.Lifetime.AddAction(action);

            Assert.AreEqual(2, calls);
        }

        [Test]
        public void AddAction_WithNullAction_ThrowsArgumentNullException()
        {
            // Pins a deliberate 2.0.0 break: 1.x stored the null and skipped it at invoke time — a silent
            // drop of exactly the kind that "every action runs exactly once" rules out.
            var alive = NewDefinition("null-action");
            var dead = NewTerminatedDefinition("null-action-dead");

            Assert.Throws<ArgumentNullException>(() => alive.Lifetime.AddAction(null));
            Assert.Throws<ArgumentNullException>(() => dead.Lifetime.AddAction(null));
        }

        [Test]
        public void AddAction_RegisteredFromInsideATerminationAction_InvokesTheNewActionImmediately()
        {
            // Re-entrant registration during termination must neither deadlock nor be dropped.
            // Nested two levels deep, so a one-shot fix would not pass.
            var definition = NewDefinition("reentrant");
            var lifetime = definition.Lifetime;
            var calls = 0;

            lifetime.AddAction(() =>
                lifetime.AddAction(() =>
                {
                    calls++;
                    lifetime.AddAction(() => calls++);
                }));

            definition.Terminate();

            Assert.AreEqual(2, calls);
        }

        [Test]
        public void IsTerminated_ObservedFromInsideATerminationAction_IsAlreadyTrue()
        {
            // The state flip happens under the lock, before any callback is invoked outside it.
            var definition = NewDefinition("state-during-termination");
            bool? onLifetime = null;
            bool? onDefinition = null;
            bool? alive = null;

            definition.Lifetime.AddAction(() =>
            {
                onLifetime = definition.Lifetime.IsTerminated;
                onDefinition = definition.IsTerminated;
                alive = definition.Lifetime.IsAlive();
            });

            definition.Terminate();

            Assert.AreEqual(true, onLifetime, "State must be flipped before callbacks are invoked.");
            Assert.AreEqual(true, onDefinition);
            Assert.AreEqual(false, alive, "IsAlive must agree with IsTerminated at all times.");
        }

        // ------------------------------------------------------------------------------------------
        // AddBracket
        // ------------------------------------------------------------------------------------------

        [Test]
        public void AddBracket_OnLiveLifetime_InvokesOnOpenImmediatelyAndOnTerminateExactlyOnce()
        {
            var definition = NewDefinition("bracket");
            var log = new List<string>();

            var returned = definition.Lifetime.AddBracket(
                onOpen: () => log.Add("open"),
                onTerminate: () => log.Add("close"));

            Assert.AreSame(definition.Lifetime, returned);
            CollectionAssert.AreEqual(new[] { "open" }, log, "onOpen runs immediately, onTerminate does not.");

            definition.Terminate();
            definition.Terminate();
            definition.Dispose();

            CollectionAssert.AreEqual(new[] { "open", "close" }, log);
        }

        [Test]
        public void AddBracket_OnTerminatedLifetime_InvokesNeitherCallback()
        {
            // The scope already ended, so the resource was never acquired and there is nothing to
            // release. This deliberately does NOT follow AddAction's immediate-invoke rule.
            var definition = NewTerminatedDefinition("bracket-dead");
            var log = new List<string>();

            var returned = definition.Lifetime.AddBracket(
                onOpen: () => log.Add("open"),
                onTerminate: () => log.Add("close"));

            CollectionAssert.IsEmpty(log);
            Assert.AreSame(definition.Lifetime, returned, "R12: AddBracket keeps returning the Lifetime.");
        }

        [Test]
        public void AddBracket_OnALifetimeTerminatedByItsParent_InvokesNeitherCallback()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            parent.Terminate();
            var log = new List<string>();

            child.Lifetime.AddBracket(() => log.Add("open"), () => log.Add("close"));

            CollectionAssert.IsEmpty(log);
        }

        [Test]
        public void AddBracket_WhenOnOpenThrows_NeverRegistersTheTeardown()
        {
            // onOpen is invoked BEFORE onTerminate is registered. If acquisition fails, nothing was
            // acquired, so the teardown for it must never run. (1.x registered the teardown first.)
            var definition = NewDefinition("bracket-open-throws");
            var closed = 0;
            var boom = new MarkerException("open");

            var thrown = Assert.Catch<Exception>(() =>
                definition.Lifetime.AddBracket(() => throw boom, () => closed++));

            Assert.AreSame(boom, thrown);

            definition.Terminate();

            Assert.AreEqual(0, closed, "A failed acquisition must not leave a teardown registered.");
        }

        [Test]
        public void AddBracket_WithNullOnOpen_RegistersTheTeardownOnlyWhileTheScopeIsOpen()
        {
            // A null onOpen is legal and is what makes AddBracket distinct from AddAction: it means
            // "register this, but only if the scope is still open".
            var alive = NewDefinition("bracket-null-open");
            var dead = NewTerminatedDefinition("bracket-null-open-dead");
            var closedAlive = 0;
            var closedDead = 0;

            alive.Lifetime.AddBracket(null, () => closedAlive++);
            dead.Lifetime.AddBracket(null, () => closedDead++);
            Assert.AreEqual(0, closedAlive);

            alive.Terminate();
            Assert.AreEqual(1, closedAlive);
            Assert.AreEqual(0, closedDead);
        }

        [Test]
        public void AddBracket_WithNullOnTerminate_ThrowsBeforeAnythingIsAcquired()
        {
            // A bracket that cannot release must not open, so the argument is validated before onOpen
            // runs — and before the liveness check, so the contract does not depend on state.
            var alive = NewDefinition("bracket-null-close");
            var dead = NewTerminatedDefinition("bracket-null-close-dead");
            var opened = 0;

            Assert.Throws<ArgumentNullException>(() => alive.Lifetime.AddBracket(() => opened++, null));
            Assert.Throws<ArgumentNullException>(() => dead.Lifetime.AddBracket(() => opened++, null));

            Assert.AreEqual(0, opened, "Validation must happen before the resource is acquired.");
        }

        [Test]
        public void AddBracket_AndAddAction_ShareOneReverseOrderedTerminationSequence()
        {
            // Inner resources close before outer ones, and brackets interleave with plain actions.
            var definition = NewDefinition("mixed-lifo");
            var log = new List<string>();

            definition.Lifetime.AddAction(() => log.Add("action-1"));
            definition.Lifetime.AddBracket(() => log.Add("open-outer"), () => log.Add("close-outer"));
            definition.Lifetime.AddBracket(() => log.Add("open-inner"), () => log.Add("close-inner"));
            definition.Lifetime.AddAction(() => log.Add("action-2"));

            definition.Terminate();

            CollectionAssert.AreEqual(
                new[] { "open-outer", "open-inner", "action-2", "close-inner", "close-outer", "action-1" },
                log);
        }

        [Test]
        public void AddBracket_NeverReleasesAResourceItNeverAcquired()
        {
            // The bracket rule stated as the invariant it exists to protect - only an acquired resource
            // is released - across the alive path, the registered-during-termination path, and the dead
            // path in one sweep.
            var definition = NewDefinition("bracket-invariant");
            var opens = new List<string>();
            var closes = new List<string>();

            definition.Lifetime.AddBracket(() => opens.Add("alive"), () => closes.Add("alive"));
            definition.Lifetime.AddAction(() =>
                definition.Lifetime.AddBracket(() => opens.Add("during"), () => closes.Add("during")));

            definition.Terminate();

            definition.Lifetime.AddBracket(() => opens.Add("after"), () => closes.Add("after"));

            CollectionAssert.IsSubsetOf(closes, opens, "No resource may be released that was never acquired.");
            CollectionAssert.AreEquivalent(opens, closes, "Every acquired resource must be released exactly once.");
            CollectionAssert.Contains(opens, "alive");
            CollectionAssert.DoesNotContain(opens, "during", "A bracket opened while terminating must do nothing.");
            CollectionAssert.DoesNotContain(opens, "after", "A bracket opened after termination must do nothing.");
        }

        // ------------------------------------------------------------------------------------------
        // Throwing termination actions
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Terminate_WhenSeveralActionsThrow_RunsAllOfThemAndAggregatesTheFailuresInTheOrderTheyOccurred()
        {
            // Two or more failures are reported together, in reverse registration order.
            var definition = NewDefinition("many-throwers");
            var order = new List<string>();
            var first = new MarkerException("first");
            var second = new MarkerException("second");
            var third = new MarkerException("third");

            definition.Lifetime.AddAction(() => order.Add("a"));
            definition.Lifetime.AddAction(() => { order.Add("t1"); throw first; });
            definition.Lifetime.AddAction(() => order.Add("b"));
            definition.Lifetime.AddAction(() => { order.Add("t2"); throw second; });
            definition.Lifetime.AddAction(() => { order.Add("t3"); throw third; });
            definition.Lifetime.AddAction(() => order.Add("c"));

            var aggregate = Assert.Throws<AggregateException>(() => definition.Terminate(),
                "R4: two or more termination failures surface as a single AggregateException.");

            CollectionAssert.AreEqual(new Exception[] { third, second, first }, aggregate.InnerExceptions,
                "The failures are listed in the order they occurred, which is reverse registration order.");
            CollectionAssert.AreEqual(new[] { "c", "t3", "t2", "b", "t1", "a" }, order,
                "Every action runs exactly once, in reverse registration order, despite the throws.");
        }

        [Test]
        public void Terminate_WithExactlyOneThrowingAction_RethrowsThatExceptionItselfWithItsStackTrace()
        {
            // A single failure is not wrapped. It is rethrown with its own type, so the caller's
            // catch clauses match, and with the frame that threw it, so the trace points at the culprit.
            var definition = NewDefinition("single-thrower");
            var boom = new MarkerException("only");
            var otherRan = false;
            definition.Lifetime.AddAction(() => otherRan = true);
            definition.Lifetime.AddAction(() => ThrowFromNamedFrame(boom));

            var thrown = Assert.Throws<MarkerException>(() => definition.Terminate());

            Assert.AreSame(boom, thrown);
            StringAssert.Contains(nameof(ThrowFromNamedFrame), thrown.StackTrace,
                "The original throwing frame must survive the rethrow.");
            Assert.IsTrue(otherRan, "The remaining actions still ran before the failure was reported.");
            Assert.IsTrue(definition.IsTerminated);
        }

        [Test]
        public void Terminate_WithExactlyOneFailureDeepInATree_RethrowsItUnwrappedThroughEveryLevel()
        {
            // The same across nesting: each level sees exactly one failure, so none of them wraps it.
            var root = NewDefinition("tree");
            var child = root.Lifetime.DefineNested("child");
            var grandChild = child.Lifetime.DefineNested("grand-child");
            var boom = new MarkerException("deep");
            grandChild.Lifetime.AddAction(() => throw boom);
            child.Lifetime.AddAction(() => { });
            root.Lifetime.AddAction(() => { });

            var thrown = Assert.Throws<MarkerException>(() => root.Terminate());

            Assert.AreSame(boom, thrown);
            Assert.IsTrue(grandChild.IsTerminated);
        }

        [Test]
        public void Terminate_AfterActionsThrew_IsStillCompleteIdempotentAndDoesNotRethrow()
        {
            var definition = NewDefinition("throw-then-idempotent");
            var calls = 0;
            definition.Lifetime.AddAction(() => calls++);
            definition.Lifetime.AddAction(() => throw new MarkerException("boom"));

            Assert.Throws<MarkerException>(() => definition.Terminate());

            Assert.IsTrue(definition.IsTerminated, "The lifetime is terminated even though an action threw.");
            Assert.DoesNotThrow(() => definition.Terminate(), "A second Terminate must not re-run or re-throw.");
            Assert.DoesNotThrow(() => definition.Dispose());
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void Terminate_WhenAnActionThrows_StillTerminatesTheChildLifetimes()
        {
            var parent = NewDefinition("parent");
            parent.Lifetime.AddAction(() => throw new MarkerException("boom"));
            var child = parent.Lifetime.DefineNested("child");

            Assert.Throws<MarkerException>(() => parent.Terminate());

            Assert.IsTrue(child.IsTerminated);
        }

        [Test]
        public void Terminate_WhenAChildActionThrows_ParentStillRunsItsOwnRemainingActions()
        {
            // The throw happens inside a cascaded child termination; the parent's remaining actions must
            // still run, and the child's exception must still reach whoever terminated the parent.
            var parent = NewDefinition("parent");
            var log = new List<string>();
            var boom = new MarkerException("from-child");

            parent.Lifetime.AddAction(() => log.Add("parent-first"));
            var child = parent.Lifetime.DefineNested("child");
            child.Lifetime.AddAction(() => throw boom);
            parent.Lifetime.AddAction(() => log.Add("parent-last"));

            var thrown = Assert.Catch<Exception>(() => parent.Terminate());

            CollectionAssert.AreEqual(new[] { "parent-last", "parent-first" }, log);
            Assert.AreSame(boom, thrown,
                "The child's only failure must reach whoever terminated the parent, unwrapped.");
            Assert.IsTrue(child.IsTerminated);
            Assert.IsTrue(parent.IsTerminated);
        }

        [Test]
        public void Terminate_NestsAggregatesOnlyWhereAScopeCollectedSeveralFailures()
        {
            // Documents a deliberate choice: a scope that collected two or more failures reports them as its
            // own aggregate, and its parent keeps that aggregate intact rather than flattening it, so the
            // structure stays faithful to the scope tree. AggregateException.Flatten() is one call away for
            // callers who want just the leaves.
            var parent = NewDefinition("nesting");
            var child = parent.Lifetime.DefineNested("child");
            var childFirst = new MarkerException("child-1");
            var childSecond = new MarkerException("child-2");
            var parentBoom = new MarkerException("parent");
            child.Lifetime.AddAction(() => throw childFirst);
            child.Lifetime.AddAction(() => throw childSecond);
            parent.Lifetime.AddAction(() => throw parentBoom);

            var aggregate = Assert.Throws<AggregateException>(() => parent.Terminate());

            Assert.AreEqual(2, aggregate.InnerExceptions.Count);
            Assert.AreSame(parentBoom, aggregate.InnerExceptions[0], "LIFO: the parent's own action ran first.");
            var childAggregate = aggregate.InnerExceptions[1] as AggregateException;
            Assert.IsNotNull(childAggregate, "The child's two failures arrive as the child's own aggregate.");
            CollectionAssert.AreEqual(new Exception[] { childSecond, childFirst }, childAggregate.InnerExceptions);
            CollectionAssert.AreEquivalent(
                new Exception[] { childFirst, childSecond, parentBoom },
                aggregate.Flatten().InnerExceptions.ToArray());
        }

        [Test]
        public void Terminate_DoesNotWrapAChildsSingleFailureWhenAggregatingTheParents()
        {
            // The other half of the rule: a child that failed once contributes that exception itself.
            var parent = NewDefinition("flat");
            var child = parent.Lifetime.DefineNested("child");
            var childBoom = new MarkerException("child");
            var parentBoom = new MarkerException("parent");
            child.Lifetime.AddAction(() => throw childBoom);
            parent.Lifetime.AddAction(() => throw parentBoom);

            var aggregate = Assert.Throws<AggregateException>(() => parent.Terminate());

            CollectionAssert.AreEqual(new Exception[] { parentBoom, childBoom }, aggregate.InnerExceptions);
        }

        // ------------------------------------------------------------------------------------------
        // Reentrancy
        // ------------------------------------------------------------------------------------------

        [Test]
        public void TerminationAction_ThatTerminatesItsOwnLifetime_DoesNotRecurseAndDoesNotSkipTheRest()
        {
            var definition = NewDefinition("self-terminate");
            var order = new List<string>();

            definition.Lifetime.AddAction(() => order.Add("first"));
            definition.Lifetime.AddAction(() =>
            {
                order.Add("self");
                definition.Terminate();
            });
            definition.Lifetime.AddAction(() => order.Add("last"));

            definition.Terminate();

            CollectionAssert.AreEqual(new[] { "last", "self", "first" }, order);
            Assert.IsTrue(definition.IsTerminated);
        }

        [Test]
        public void TerminationAction_ThatTerminatesItsParent_CompletesAndRunsEachActionOnce()
        {
            // child -> parent is exactly the direction that inverts against AddDefinition's parent -> child.
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            var parentRuns = 0;
            var childRuns = 0;

            parent.Lifetime.AddAction(() => parentRuns++);
            child.Lifetime.AddAction(() =>
            {
                childRuns++;
                parent.Terminate();
            });

            child.Terminate();

            Assert.AreEqual(1, childRuns);
            Assert.AreEqual(1, parentRuns);
            Assert.IsTrue(parent.IsTerminated);
            Assert.IsTrue(child.IsTerminated);
        }

        [Test]
        public void MutuallyTerminatingParentAndChild_SettleWithEveryActionHavingRunOnce()
        {
            var parent = NewDefinition("mutual-parent");
            var child = parent.Lifetime.DefineNested("mutual-child");
            var parentRuns = 0;
            var childRuns = 0;

            parent.Lifetime.AddAction(() => { parentRuns++; child.Terminate(); });
            child.Lifetime.AddAction(() => { childRuns++; parent.Terminate(); });

            parent.Terminate();

            Assert.AreEqual(1, parentRuns);
            Assert.AreEqual(1, childRuns);
        }

        // ------------------------------------------------------------------------------------------
        // Unregistering via a nested definition
        // ------------------------------------------------------------------------------------------

        [Test]
        public void TerminatingANestedDefinition_IsTheSupportedWayToUnregisterAnAction()
        {
            // There is deliberately no RemoveAction API — a nested definition IS the removal mechanism.
            var owner = NewDefinition("owner");
            var permanentRuns = 0;
            var temporaryRuns = 0;

            owner.Lifetime.AddAction(() => permanentRuns++);

            var subscription = owner.Lifetime.DefineNested("subscription");
            subscription.Lifetime.AddAction(() => temporaryRuns++);

            subscription.Terminate();
            Assert.AreEqual(1, temporaryRuns, "Terminating the nested definition runs its teardown once...");

            owner.Terminate();
            Assert.AreEqual(1, temporaryRuns, "...and never again when the owner dies.");
            Assert.AreEqual(1, permanentRuns);
        }

        [Test]
        public void TerminatedNestedDefinition_IsNotTouchedAgainWhenTheParentTerminatesLater()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            var childRuns = 0;
            child.Lifetime.AddAction(() => childRuns++);

            child.Terminate();
            Assert.AreEqual(1, childRuns);

            var sibling = parent.Lifetime.DefineNested("sibling");
            var siblingRuns = 0;
            sibling.Lifetime.AddAction(() => siblingRuns++);

            parent.Terminate();

            Assert.AreEqual(1, childRuns, "The already terminated child must not be touched again.");
            Assert.AreEqual(1, siblingRuns, "The still-live sibling must be terminated by the parent.");
        }

        [Test]
        public void TerminatingANestedDefinition_ReleasesTheParentsReferenceToIt()
        {
            // The detach registered by AddDefinition must really drop the child's entry from the
            // parent's list; otherwise a long-lived parent accumulates dead children. Non-retention is the
            // only observable proof of removal, since a stale entry would merely be an idempotent no-op.
            var parent = NewDefinition("parent");

            var weakChild = CreateAndTerminateChild(parent.Lifetime);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsFalse(weakChild.IsAlive,
                "A terminated nested definition must no longer be referenced by its parent.");
            GC.KeepAlive(parent);
        }

        /// <summary>
        /// Runs the set-up of a retention test on a thread of its own and waits for it, so that nothing it
        /// created is referenced from a live stack once it returns. A conservative collector, such as the Boehm
        /// GC of Unity's editor, treats anything on a live stack that looks like a pointer as a root; the stack
        /// of a finished thread is not scanned. (Measured for the signal package's retention tests on the Mono
        /// of Unity 6000.0.41f1: created on the calling thread, an unreachable object survived 19 of 20
        /// collections; created on a thread of its own, none of 20.)
        /// </summary>
        private static T OnAThreadOfItsOwn<T>(Func<T> setUp)
        {
            var result = default(T);
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = setUp();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }) { IsBackground = true, Name = "retention-set-up" };

            thread.Start();
            Assert.IsTrue(thread.Join(30000), "the set-up thread did not finish");
            if (failure != null) Assert.Fail("the set-up threw: " + failure);
            return result;
        }

        private static void CollectGarbage()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateAndTerminateChild(Lifetime parent)
        {
            var child = parent.DefineNested("short-lived");
            child.Lifetime.AddAction(() => { });
            child.Terminate();
            return new WeakReference(child);
        }

        [Test]
        public void TerminatingAChildBetweenLiveSiblings_ReleasesTheParentsReferenceToIt()
        {
            // A child detached from the middle of the parent's sequence leaves an empty slot rather
            // than shifting its siblings. The slot must not keep the child reachable.
            var parent = NewDefinition("parent");
            var before = parent.Lifetime.DefineNested("before");
            var weakChild = CreateAndTerminateChild(parent.Lifetime);
            var after = parent.Lifetime.DefineNested("after");

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsFalse(weakChild.IsAlive,
                "A terminated child between live siblings must no longer be referenced by its parent.");
            Assert.IsFalse(before.IsTerminated);
            Assert.IsFalse(after.IsTerminated);
            GC.KeepAlive(parent);
        }

        [Test]
        public void ChildrenMovedByCompaction_AreReleasedByTheParentOnceTheyTerminate()
        {
            // Compacting in place moves the surviving entries down and must clear the
            // slots they vacated. A stale copy past the used range is never run, so only non-retention can
            // show it: it would keep a moved child reachable after that child terminated.
            var parent = NewDefinition("parent");

            var weakChildren = CompactInPlaceThenTerminateTheMovedChildren(parent.Lifetime);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsTrue(weakChildren.All(weak => !weak.IsAlive),
                "Every terminated child, including the ones compaction moved, must be released by the parent.");

            var calls = 0;
            parent.Lifetime.AddAction(() => calls++);
            parent.Terminate();
            Assert.AreEqual(1, calls, "The parent must stay usable after compaction.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] CompactInPlaceThenTerminateTheMovedChildren(Lifetime parent)
        {
            var children = new Lifetime.Definition[10];
            for (var i = 0; i < children.Length; i++)
            {
                children[i] = parent.DefineNested("child-" + i);
            }

            // Oldest first: the sixth detach leaves more than half of the ten slots empty, which compacts the
            // four survivors into slots 0-3 of the same (small) array.
            for (var i = 0; i < 6; i++)
            {
                children[i].Terminate();
            }

            // Then the moved ones end too, so nothing in the parent should refer to any child any more.
            for (var i = 6; i < children.Length; i++)
            {
                children[i].Terminate();
            }

            var weak = children.Select(child => new WeakReference(child)).ToArray();

            // A conservative collector (Unity's Mono) may see a stale copy of the array reference on the
            // stack; empty it so that such a copy cannot keep all ten children alive.
            Array.Clear(children, 0, children.Length);
            return weak;
        }

        [Test]
        public void DetachingChildrenInAnyOrder_KeepsEveryRemainingEntryInStrictLifoOrder([Values(1, 2, 3)] int seed)
        {
            // Detach is O(1) via slots, empty slots and compaction, all of which move entries around
            // inside the parent. A model-based check: random registrations and early terminations against a
            // plain list of what should remain, then the parent's teardown must match the model exactly -
            // every survivor once, in reverse registration order, and no early-terminated child again.
            //
            // The run alternates 500-step phases: growth (10% actions, 70% children, 20% early terminations)
            // and shrinkage (no actions, 20% children, 80% early terminations), so that every seed drives
            // the parent through several in-place compactions and several compactions into a smaller array,
            // and then detaches children whose slots those compactions moved. (A uniform 30/40/30 mix
            // compacted at most once per run and usually never, so it could not catch a broken slot
            // rewrite or a miscounted empty slot.)
            var random = new Random(seed);
            var parent = NewDefinition("model-" + seed);
            var log = new List<string>();
            var model = new List<string>();
            var liveChildren = new List<KeyValuePair<string, Lifetime.Definition>>();

            for (var step = 0; step < 3000; step++)
            {
                var shrinking = step / 500 % 2 == 1;
                var actionBelow = shrinking ? 0 : 1;
                var childBelow = shrinking ? 2 : 8;
                var roll = random.Next(10);
                var label = "e" + step;
                if (roll < actionBelow)
                {
                    parent.Lifetime.AddAction(() => log.Add(label));
                    model.Add(label);
                }
                else if (roll < childBelow || liveChildren.Count == 0)
                {
                    var child = parent.Lifetime.DefineNested(label);
                    child.Lifetime.AddAction(() => log.Add(label));
                    model.Add(label);
                    liveChildren.Add(new KeyValuePair<string, Lifetime.Definition>(label, child));
                }
                else
                {
                    // Early termination of a random live child: logs now, and must vanish from the parent.
                    var index = random.Next(liveChildren.Count);
                    var victim = liveChildren[index];
                    liveChildren.RemoveAt(index);
                    model.Remove(victim.Key);

                    var logged = log.Count;
                    victim.Value.Terminate();
                    CollectionAssert.AreEqual(new[] { victim.Key }, log.Skip(logged).ToArray());
                }
            }

            log.Clear();
            parent.Terminate();

            model.Reverse();
            CollectionAssert.AreEqual(model, log,
                "The parent's teardown must run exactly the surviving entries, once each, in LIFO order.");
            Assert.IsTrue(liveChildren.All(pair => pair.Value.IsTerminated));
        }

        [Test]
        public void RepeatedlyCreatingAndTerminatingNestedScopes_LeavesTheParentUsable()
        {
            // The container's per-scope teardown pattern: it must stay correct over many iterations.
            var parent = NewDefinition("parent");
            var totalCalls = 0;

            for (var i = 0; i < 1000; i++)
            {
                var scope = parent.Lifetime.DefineNested();
                scope.Lifetime.AddAction(() => totalCalls++);
                scope.Terminate();
            }

            Assert.AreEqual(1000, totalCalls);
            Assert.IsFalse(parent.IsTerminated);

            parent.Terminate();
            Assert.AreEqual(1000, totalCalls, "No dead scope may be re-run when the parent dies.");
        }

        [Test]
        public void FreshLifetimes_DoNotInheritActionsFromPreviouslyTerminatedOnes()
        {
            // No shared mutable state between Lifetime instances: 1.2.0 kept a shared pool of action
            // lists, which could hand one List<Action> to two live Lifetime instances.
            //
            // What the test is after is that a churned action never runs a SECOND time, from a LATER
            // lifetime, so the poison is counted rather than fatal. An `Assert.Fail(...)` registered on each
            // churned definition would fire at that definition's own termination and fail against any
            // correct implementation.
            const int churn = 50;
            var churnedRuns = 0;

            for (var i = 0; i < churn; i++)
            {
                var recycled = NewDefinition();
                recycled.Lifetime.AddAction(() => churnedRuns++);
                recycled.Terminate();
            }

            Assert.AreEqual(churn, churnedRuns, "Each churned lifetime runs exactly its own single action.");

            var first = NewDefinition("first");
            var second = NewDefinition("second");
            var firstCalls = 0;
            var secondCalls = 0;
            first.Lifetime.AddAction(() => firstCalls++);
            second.Lifetime.AddAction(() => secondCalls++);

            first.Terminate();
            Assert.AreEqual(1, firstCalls);
            Assert.AreEqual(0, secondCalls, "Terminating one lifetime must not fire another's actions.");

            second.Terminate();
            Assert.AreEqual(1, firstCalls);
            Assert.AreEqual(1, secondCalls);
            Assert.AreEqual(churn, churnedRuns,
                "A fresh lifetime must not inherit a recycled action list from a terminated one.");
        }

        // ------------------------------------------------------------------------------------------
        // LifetimeExtensions: With
        // ------------------------------------------------------------------------------------------

        [Test]
        public void With_DisposesTheDisposableExactlyOnceWhenTheLifetimeTerminates()
        {
            var definition = NewDefinition("with-live");
            var disposable = new CountingDisposable();

            var returned = disposable.With(definition.Lifetime);

            Assert.AreSame(disposable, returned, "With must return the same instance for fluent use.");
            Assert.AreEqual(0, disposable.DisposeCount, "Nothing is disposed while the scope is open.");

            definition.Terminate();
            definition.Terminate();
            definition.Dispose();

            Assert.AreEqual(1, disposable.DisposeCount);
        }

        [Test]
        public void With_ReturnsTheStaticTypeItWasGiven()
        {
            // With<T> returns T, so construction, ownership and use fit in one declaration. The
            // declarations below are the assertion - they do not compile against the 1.x signature, which
            // returned IDisposable.
            var definition = NewDefinition("with-typed");

            CountingDisposable counting = new CountingDisposable().With(definition.Lifetime);
            IDisposable asInterface = new CountingDisposable();
            IDisposable returned = asInterface.With(definition.Lifetime);

            Assert.AreSame(asInterface, returned);
            Assert.AreEqual(0, counting.DisposeCount);

            definition.Terminate();

            Assert.AreEqual(1, counting.DisposeCount);
            Assert.AreEqual(1, ((CountingDisposable)asInterface).DisposeCount);
        }

        [Test]
        public void With_OnTerminatedLifetime_DisposesImmediatelyAndExactlyOnce()
        {
            // In 1.x the registration was silently dropped and the disposable was never disposed.
            var definition = NewTerminatedDefinition("with-dead");
            var disposable = new CountingDisposable();

            var returned = disposable.With(definition.Lifetime);

            Assert.AreEqual(1, disposable.DisposeCount, "A dead scope disposes the resource at once.");
            Assert.AreSame(disposable, returned);

            definition.Terminate();
            Assert.AreEqual(1, disposable.DisposeCount, "Exactly once, ever.");
        }

        [Test]
        public void With_WhenDisposeThrowsOnADeadLifetime_SurfacesTheFailureToTheCaller()
        {
            var definition = NewTerminatedDefinition("with-throwing");
            var disposable = new ThrowingDisposable();

            var thrown = Assert.Catch<Exception>(() => disposable.With(definition.Lifetime));

            Assert.AreSame(disposable.Error, thrown);
        }

        [Test]
        public void With_DisposesNestedRegistrationsInReverseOrder()
        {
            var definition = NewDefinition("with-lifo");
            var log = new List<string>();

            new ActionDisposable(() => log.Add("outer")).With(definition.Lifetime);
            new ActionDisposable(() => log.Add("inner")).With(definition.Lifetime);
            definition.Terminate();

            CollectionAssert.AreEqual(new[] { "inner", "outer" }, log);
        }

        // ------------------------------------------------------------------------------------------
        // LifetimeExtensions: AsCancellationToken
        // ------------------------------------------------------------------------------------------

        [Test]
        public void AsCancellationToken_OnLiveLifetime_ReturnsACancellableUncancelledToken()
        {
            var definition = NewDefinition("cts-live");

            var token = definition.Lifetime.AsCancellationToken();

            Assert.IsTrue(token.CanBeCanceled, "The token must be able to observe the lifetime ending.");
            Assert.IsFalse(token.IsCancellationRequested);
            Assert.DoesNotThrow(() => token.ThrowIfCancellationRequested());
        }

        [Test]
        public void AsCancellationToken_CancelsEveryTokenHandedOutWhenTheLifetimeTerminates()
        {
            var definition = NewDefinition("cts-many");
            var first = definition.Lifetime.AsCancellationToken();
            var second = definition.Lifetime.AsCancellationToken();

            definition.Terminate();

            Assert.IsTrue(first.IsCancellationRequested);
            Assert.IsTrue(second.IsCancellationRequested);
            // Assert.Catch, not Assert.Throws: the BCL only promises an OperationCanceledException, and a
            // derived type would be a conforming implementation.
            Assert.Catch<OperationCanceledException>(() => first.ThrowIfCancellationRequested());
        }

        [Test]
        public void AsCancellationToken_OnTerminatedLifetime_ReturnsAnAlreadyCancelledToken()
        {
            // In 1.x the registration was dropped, the token never cancelled, and any await on
            // it hung forever.
            var definition = NewTerminatedDefinition("cts-dead");

            var token = definition.Lifetime.AsCancellationToken();

            Assert.IsTrue(token.CanBeCanceled);
            Assert.IsTrue(token.IsCancellationRequested, "A dead lifetime must hand out a cancelled token.");
            Assert.Catch<OperationCanceledException>(() => token.ThrowIfCancellationRequested());
        }

        [Test]
        public void AsCancellationToken_OnALifetimeTerminatedByItsParent_ReturnsAnAlreadyCancelledToken()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");
            parent.Terminate();

            Assert.IsTrue(child.Lifetime.AsCancellationToken().IsCancellationRequested);
        }

        [Test]
        public void AsCancellationToken_LeavesTheTokenFullyUsableAfterTermination()
        {
            // This is the observable that distinguishes "the CancellationTokenSource was disposed" from
            // "it was not". Disposing it would break every caller still holding the token, so we never do.
            var definition = NewDefinition("cts-after-terminate");
            var token = definition.Lifetime.AsCancellationToken();

            definition.Terminate();

            Assert.IsTrue(token.IsCancellationRequested);
            Assert.DoesNotThrow(() =>
            {
                var _ = token.CanBeCanceled;
                var __ = token.WaitHandle;
            });

            var callbackRuns = 0;
            Assert.DoesNotThrow(() => token.Register(() => callbackRuns++),
                "Registering on the token after termination must not throw ObjectDisposedException.");
            Assert.AreEqual(1, callbackRuns,
                "A callback registered on an already cancelled token runs immediately.");
        }

        // ------------------------------------------------------------------------------------------
        // LifetimeExtensions: IsAlive / ThrowIfTerminated, and the null contract
        // ------------------------------------------------------------------------------------------

        [Test]
        public void IsAlive_IsAlwaysTheNegationOfIsTerminated()
        {
            var definition = NewDefinition("is-alive");
            Assert.IsTrue(definition.Lifetime.IsAlive());
            Assert.AreEqual(!definition.Lifetime.IsTerminated, definition.Lifetime.IsAlive());

            definition.Terminate();

            Assert.IsFalse(definition.Lifetime.IsAlive());
            Assert.AreEqual(!definition.Lifetime.IsTerminated, definition.Lifetime.IsAlive());
        }

        [Test]
        public void IsAlive_IsFalseForALifetimeTerminatedByItsParent()
        {
            var parent = NewDefinition("parent");
            var child = parent.Lifetime.DefineNested("child");

            parent.Terminate();

            Assert.IsFalse(child.Lifetime.IsAlive());
        }

        [Test]
        public void ThrowIfTerminated_ThrowsObjectDisposedExceptionOnlyAfterTermination()
        {
            var definition = NewDefinition("throw-if-terminated");
            var child = definition.Lifetime.DefineNested("child");

            Assert.DoesNotThrow(() => definition.Lifetime.ThrowIfTerminated());
            Assert.DoesNotThrow(() => Lifetime.Eternal.ThrowIfTerminated());

            definition.Terminate();

            Assert.Throws<ObjectDisposedException>(() => definition.Lifetime.ThrowIfTerminated());
            Assert.Throws<ObjectDisposedException>(() => child.Lifetime.ThrowIfTerminated());
        }

        [Test]
        public void LifetimeExtensions_OnANullLifetimeOrDisposable_ThrowArgumentNullException()
        {
            Lifetime lifetime = null;
            IDisposable disposable = new CountingDisposable();

            Assert.Throws<ArgumentNullException>(() => lifetime.IsAlive());
            Assert.Throws<ArgumentNullException>(() => lifetime.ThrowIfTerminated());
            Assert.Throws<ArgumentNullException>(() => lifetime.AsCancellationToken());
            Assert.Throws<ArgumentNullException>(() => disposable.With(lifetime));
            Assert.Throws<ArgumentNullException>(() => ((IDisposable)null).With(_root.Lifetime));
            Assert.AreEqual(0, ((CountingDisposable)disposable).DisposeCount);
        }
    }
}
