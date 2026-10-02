using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace OpenUGD.Tests
{
    /// <summary>
    /// Behavioural suite for OpenUGD.Lifetime 2.0.0 — deterministic, single threaded, fast.
    /// Concurrency and stress live in LifetimeConcurrencyTests so that this fixture stays instant to run.
    ///
    /// State hygiene: Lifetime.Eternal is a process-wide static that never terminates, so anything
    /// registered on it directly would live for the whole test run. Every test therefore hangs its
    /// lifetimes off a per-test root definition that TearDown terminates; definitions that implicitly
    /// parent onto Eternal (Lifetime.Intersection does) are tracked and terminated too, which also
    /// unregisters them from Eternal.
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
            // Terminate Eternal-parented definitions first, then the per-test root. Some tests register
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

        /// <summary>Registers an Eternal-parented definition for termination in TearDown.</summary>
        private Lifetime.Definition Track(Lifetime.Definition definition)
        {
            _tracked.Add(definition);
            return definition;
        }

        /// <summary>A fresh, live definition owned by this test.</summary>
        private Lifetime.Definition NewDefinition(string id = null) => _root.Lifetime.DefineNested(id);

        /// <summary>A fresh, already terminated definition owned by this test.</summary>
        private Lifetime.Definition NewTerminatedDefinition(string id = null)
        {
            var definition = _root.Lifetime.DefineNested(id);
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
        /// Flattens whatever Terminate() threw into its leaf exceptions, so assertions do not depend on how
        /// deeply nested scopes wrap their AggregateExceptions.
        /// </summary>
        private static Exception[] Leaves(Exception exception)
        {
            return exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.ToArray()
                : new[] { exception };
        }

        // ------------------------------------------------------------------------------------------
        // Eternal, Id / ParentId (R12: debugging aids, but part of the public API)
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
        public void DefinitionId_IsTheSuppliedDebugIdAndIsNullWhenOmitted()
        {
            var named = NewDefinition("my-debug-id");
            var unnamed = NewDefinition();

            Assert.AreEqual("my-debug-id", named.Id);
            Assert.IsNull(unnamed.Id, "An omitted debug id must stay null rather than being invented.");
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
        // Define / DefineNested
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Define_WithNullLifetime_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => Lifetime.Define(null));
            Assert.Throws<ArgumentNullException>(() => Lifetime.Definition.Define(null));
        }

        [Test]
        public void Define_OnTerminatedLifetime_ThrowsInvalidOperationException()
        {
            var dead = NewTerminatedDefinition("dead-parent");

            Assert.Throws<InvalidOperationException>(() => Lifetime.Define(dead.Lifetime, "child"));
            Assert.Throws<InvalidOperationException>(() => dead.Lifetime.DefineNested("child"));
        }

        [Test]
        public void DefineNested_FromInsideATerminationAction_ThrowsRatherThanYieldingALiveChildOfADeadParent()
        {
            var parent = NewDefinition("define-during-termination");
            Exception caught = null;

            parent.Lifetime.AddAction(() =>
            {
                try
                {
                    parent.Lifetime.DefineNested("late-child");
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
            });

            parent.Terminate();

            // IsTerminated is already true while actions run, so Define takes its normal terminated path.
            Assert.IsInstanceOf<InvalidOperationException>(caught);
        }

        [Test]
        public void Define_AndDefineNested_ProduceEquivalentChildDefinitions()
        {
            var parent = NewDefinition("parent");

            var viaStatic = Lifetime.Define(parent.Lifetime, "static");
            var viaInstance = parent.Lifetime.DefineNested("instance");

            Assert.IsFalse(viaStatic.IsTerminated);
            Assert.IsFalse(viaInstance.IsTerminated);
            Assert.AreEqual(parent.Lifetime.Id, viaStatic.ParentId);
            Assert.AreEqual(parent.Lifetime.Id, viaInstance.ParentId);
            Assert.AreNotSame(viaStatic.Lifetime, viaInstance.Lifetime);
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
            // R6: duplicates are no longer rejected, so intersecting one lifetime with itself must be
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
        public void Intersection_WithANullArrayOrANullElement_ThrowsArgumentNullException()
        {
            // Added in the merge: neither blind suite covered this. 1.x threw NullReferenceException
            // part-way through wiring, leaving a definition attached to Eternal forever.
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
        // Terminate / Dispose / using / implicit conversion (R7, R8)
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
            // R7: a single public Dispose() must still satisfy IDisposable after the redundant explicit
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

        [Test]
        public void ImplicitConversion_LetsADefinitionBePassedWhereALifetimeIsExpected()
        {
            var definition = NewDefinition("implicit-arg");
            var calls = 0;

            var child = Lifetime.Define(definition, "child-of-definition");
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
        // AddAction (R1, R6, R10, R12)
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
            // R10 + R12.
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
            // R1: every action handed to a Lifetime runs exactly once, no matter when it was handed over.
            // This REPLACES the 1.x test AddAction_OnTerminatedLifetime_DoesNothing, which asserted that the
            // action was silently dropped.
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
            // R1: the immediate invocation is a real invocation, so its failure must reach the call site
            // rather than being swallowed — swallowing is the very class of bug R1 removes.
            var definition = NewTerminatedDefinition("dead-throwing");
            var boom = new MarkerException("immediate");

            var thrown = Assert.Catch<Exception>(() => definition.Lifetime.AddAction(() => throw boom));

            Assert.AreSame(boom, thrown, "The original exception must propagate unwrapped.");
        }

        [Test]
        public void AddAction_WithTheSameDelegateTwice_RegistersTwiceAndInvokesItTwice()
        {
            // R6: duplicates are permitted and run once per registration (multicast semantics).
            // This REPLACES the 1.x test AddAction_SameActionTwice_ThrowsArgumentException.
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
            // Added in the merge: neither blind suite covered it, and it pins a deliberate 2.0.0 break.
            // 1.x stored the null and skipped it at invoke time — a silent drop of exactly the kind R1 kills.
            var alive = NewDefinition("null-action");
            var dead = NewTerminatedDefinition("null-action-dead");

            Assert.Throws<ArgumentNullException>(() => alive.Lifetime.AddAction(null));
            Assert.Throws<ArgumentNullException>(() => dead.Lifetime.AddAction(null));
        }

        [Test]
        public void AddAction_RegisteredFromInsideATerminationAction_InvokesTheNewActionImmediately()
        {
            // R1 + R3: re-entrant registration during termination must neither deadlock nor be dropped.
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
            // R3: the state flip happens under the lock, before any callback is invoked outside it.
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
        // AddBracket (R2, R10)
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
            // R2: the scope already ended, so the resource was never acquired and there is nothing to
            // release. This deliberately does NOT follow R1's immediate-invoke rule.
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
            // R2: onOpen is invoked BEFORE onTerminate is registered. If acquisition fails, nothing was
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
            // Added in the merge: neither blind suite covered it. A bracket that cannot release must not
            // open, so the argument is validated before onOpen runs — and before the liveness check, so the
            // contract does not depend on state.
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
            // R10: inner resources close before outer ones, and brackets interleave with plain actions.
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
            // R2 stated as the invariant it exists to protect, across the alive path, the
            // registered-during-termination path, and the dead path in one sweep.
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
        // Throwing termination actions (R4)
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Terminate_WhenActionsThrow_StillRunsAllOfThemAndCollectsEveryException()
        {
            // R4 + R10.
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

            var thrown = Assert.Catch<Exception>(() => definition.Terminate());

            Assert.IsInstanceOf<AggregateException>(thrown,
                "R4: termination failures surface as a single AggregateException.");
            CollectionAssert.AreEquivalent(new Exception[] { first, second, third }, Leaves(thrown));
            CollectionAssert.AreEqual(new[] { "c", "t3", "t2", "b", "t1", "a" }, order,
                "Every action runs exactly once, in reverse registration order, despite the throws.");
        }

        [Test]
        public void Terminate_WithOneThrowingAction_StillReportsItAsAnAggregateException()
        {
            // R4 read literally: one collected exception still uses the AggregateException shape, so callers
            // have exactly one shape to handle.
            var definition = NewDefinition("single-thrower");
            var boom = new MarkerException("only");
            definition.Lifetime.AddAction(() => throw boom);

            var aggregate = Assert.Throws<AggregateException>(() => definition.Terminate());

            Assert.AreEqual(1, aggregate.InnerExceptions.Count);
            Assert.AreSame(boom, aggregate.InnerExceptions[0]);
        }

        [Test]
        public void Terminate_AfterActionsThrew_IsStillCompleteIdempotentAndDoesNotRethrow()
        {
            var definition = NewDefinition("throw-then-idempotent");
            var calls = 0;
            definition.Lifetime.AddAction(() => calls++);
            definition.Lifetime.AddAction(() => throw new MarkerException("boom"));

            Assert.Catch<AggregateException>(() => definition.Terminate());

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

            Assert.Throws<AggregateException>(() => parent.Terminate());

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
            CollectionAssert.Contains(Leaves(thrown), boom,
                "The child's exception must be reported to whoever terminated the parent.");
            Assert.IsTrue(child.IsTerminated);
            Assert.IsTrue(parent.IsTerminated);
        }

        [Test]
        public void Terminate_NestsAggregatesAlongTheScopeTreeAndFlattensToTheLeaves()
        {
            // Documents a deliberate choice: nested scopes produce nested AggregateExceptions rather than
            // one flattened list, because that keeps the structure faithful to the scope tree.
            // AggregateException.Flatten() is one call away for callers who want just the leaves.
            var parent = NewDefinition("nesting");
            var child = parent.Lifetime.DefineNested("child");
            var childBoom = new MarkerException("child");
            var parentBoom = new MarkerException("parent");
            child.Lifetime.AddAction(() => throw childBoom);
            parent.Lifetime.AddAction(() => throw parentBoom);

            var aggregate = Assert.Throws<AggregateException>(() => parent.Terminate());

            Assert.IsTrue(aggregate.InnerExceptions.Any(e => e is AggregateException),
                "The child's failure arrives wrapped in its own aggregate.");
            CollectionAssert.AreEquivalent(
                new Exception[] { childBoom, parentBoom },
                aggregate.Flatten().InnerExceptions.ToArray());
        }

        // ------------------------------------------------------------------------------------------
        // Reentrancy (R3)
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
        // Unregistering via a nested definition (R11)
        // ------------------------------------------------------------------------------------------

        [Test]
        public void TerminatingANestedDefinition_IsTheSupportedWayToUnregisterAnAction()
        {
            // R11: there is deliberately no RemoveAction API — a nested definition IS the removal mechanism.
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
            // R11: the detach registered by AddDefinition must really drop the child's entry from the
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateAndTerminateChild(Lifetime parent)
        {
            var child = parent.DefineNested("short-lived");
            child.Lifetime.AddAction(() => { });
            child.Terminate();
            return new WeakReference(child);
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
            // R5: the deleted pool could hand one List<Action> to two live Lifetime instances.
            //
            // ADJUSTED from the blind version, which registered `Assert.Fail(...)` on each churned
            // definition and then terminated that same definition - so the poison action fired at its own
            // termination and the test failed against any correct implementation, 1.x included. What the
            // test is actually after is that a churned action never runs a SECOND time, from a LATER
            // lifetime, so the poison is counted rather than fatal.
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
        // LifetimeExtensions: With (R9)
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
        public void With_OnTerminatedLifetime_DisposesImmediatelyAndExactlyOnce()
        {
            // R9 via R1: in 1.x the registration was silently dropped and the disposable was never disposed.
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
        // LifetimeExtensions: AsCancellationToken (R9)
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
            // derived type would be a conforming implementation. The two blind suites disagreed on this.
            Assert.Catch<OperationCanceledException>(() => first.ThrowIfCancellationRequested());
        }

        [Test]
        public void AsCancellationToken_OnTerminatedLifetime_ReturnsAnAlreadyCancelledToken()
        {
            // R9 via R1: in 1.x the registration was dropped, the token never cancelled, and any await on
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
            // R9: this is the observable that distinguishes "the CancellationTokenSource was disposed" from
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
        // LifetimeExtensions: IsAlive / ThrowIfTerminated, and the null contract (R9)
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
