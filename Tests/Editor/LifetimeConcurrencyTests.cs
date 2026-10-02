using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUGD.Tests
{
    /// <summary>
    /// Concurrency and stress suite for OpenUGD.Lifetime 2.0.0. Split out from LifetimeTests so the
    /// behavioural suite stays instant; filter this one out with the "Concurrency" category when iterating.
    ///
    /// No Thread.Sleep anywhere: races are driven by Barrier plus repetition counts, so the suite is
    /// deterministic enough for CI and runs in a couple of seconds. Anything that could deadlock runs
    /// through RunWithTimeout, so a lock inversion fails the test instead of hanging the run.
    /// </summary>
    [TestFixture]
    [Category("Concurrency")]
    public class LifetimeConcurrencyTests
    {
        private Lifetime.Definition _root;
        private List<Lifetime.Definition> _tracked;

        [SetUp]
        public void SetUp()
        {
            _root = Lifetime.Eternal.DefineNested("concurrency-root");
            _tracked = new List<Lifetime.Definition>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var definition in _tracked)
            {
                try
                {
                    definition.Terminate();
                }
                catch (Exception)
                {
                    // Cleanup must never mask the real assertion failure.
                }
            }

            _tracked.Clear();

            try
            {
                _root.Terminate();
            }
            catch (Exception)
            {
                // see above
            }

            _root = null;
        }

        /// <summary>
        /// Runs <paramref name="body"/> on a worker and fails, instead of hanging CI, if it does not finish
        /// in time. Exceptions from the body are rethrown with their original type.
        /// </summary>
        private static void RunWithTimeout(Action body, int milliseconds = 20000, string message = null)
        {
            var task = Task.Run(body);
            if (!task.Wait(milliseconds))
            {
                Assert.Fail(message ?? $"Operation did not complete within {milliseconds} ms - probable deadlock.");
            }

            task.GetAwaiter().GetResult();
        }

        // ------------------------------------------------------------------------------------------
        // R3: no lock is held across a call out
        // ------------------------------------------------------------------------------------------

        [Test]
        public void Terminate_DoesNotHoldItsLockWhileInvokingUserCallbacks()
        {
            // If the lock were still held, the worker below would block until the callback returned - and
            // the callback is waiting for the worker, so the wait would time out.
            var definition = _root.Lifetime.DefineNested("no-lock-during-callbacks");
            var lifetime = definition.Lifetime;
            var workerFinished = false;
            var observedTerminated = false;
            var observedImmediateInvoke = false;

            lifetime.AddAction(() =>
            {
                using (var done = new ManualResetEventSlim(false))
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            observedTerminated = lifetime.IsTerminated;
                            var invoked = false;
                            lifetime.AddAction(() => invoked = true); // R1: immediate
                            observedImmediateInvoke = invoked;
                        }
                        finally
                        {
                            // ReSharper disable once AccessToDisposedClosure - the outer scope waits below.
                            done.Set();
                        }
                    });
                    workerFinished = done.Wait(TimeSpan.FromSeconds(5));
                }
            });

            definition.Terminate();

            Assert.IsTrue(workerFinished,
                "Another thread must be able to touch the lifetime while termination callbacks run.");
            Assert.IsTrue(observedTerminated, "The lifetime is already terminated once callbacks run.");
            Assert.IsTrue(observedImmediateInvoke);
        }

        [Test]
        public void ConcurrentParentAndChildTermination_DoesNotDeadlockAndRunsEachActionOnce()
        {
            // R3: parent->child in AddDefinition versus child->parent in the detach closure is the ABBA
            // pair. Two threads race head-on through both directions, with a Barrier so they collide.
            const int rounds = 250;

            RunWithTimeout(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    var parent = _root.Lifetime.DefineNested("p" + round);
                    var child = parent.Lifetime.DefineNested("c" + round);
                    var parentRuns = 0;
                    var childRuns = 0;

                    parent.Lifetime.AddAction(() => Interlocked.Increment(ref parentRuns));
                    child.Lifetime.AddAction(() => Interlocked.Increment(ref childRuns));

                    using (var barrier = new Barrier(2))
                    {
                        var terminateParent = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            parent.Terminate();
                        });
                        var terminateChild = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            child.Terminate();
                        });

                        Task.WaitAll(terminateParent, terminateChild);
                    }

                    Assert.AreEqual(1, Volatile.Read(ref parentRuns), $"round {round}: parent action ran twice");
                    Assert.AreEqual(1, Volatile.Read(ref childRuns), $"round {round}: child action ran twice");
                    Assert.IsTrue(parent.IsTerminated);
                    Assert.IsTrue(child.IsTerminated);
                }
            }, 30000, "R3: concurrent parent/child termination deadlocked (lock held across a nested call).");
        }

        [Test]
        public void ConcurrentDefineAndTerminateOnTheSameParent_NeverThrowsAndNeverLeavesALiveChild()
        {
            // R3: do not call into another Lifetime while holding this one's lock.
            // LS-8: defining on a dying parent never throws. Whichever way the race falls, every child ends up
            // terminated - by the parent's cascade, or at birth - and its action runs exactly once.
            const int rounds = 100;
            const int perRound = 20;

            RunWithTimeout(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    var parent = _root.Lifetime.DefineNested("parent-" + round);
                    var errors = new ConcurrentQueue<Exception>();
                    var children = new Lifetime.Definition[perRound];
                    var runs = 0;

                    using (var barrier = new Barrier(2))
                    {
                        var definer = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            try
                            {
                                for (var i = 0; i < perRound; i++)
                                {
                                    children[i] = parent.Lifetime.DefineNested();
                                    children[i].Lifetime.AddAction(() => Interlocked.Increment(ref runs));
                                }
                            }
                            catch (Exception exception)
                            {
                                errors.Enqueue(exception);
                            }
                        });

                        var terminator = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            parent.Terminate();
                        });

                        Task.WaitAll(definer, terminator);
                    }

                    CollectionAssert.IsEmpty(errors);
                    Assert.IsTrue(parent.IsTerminated);
                    Assert.IsTrue(children.All(child => child.IsTerminated),
                        $"round {round}: no child may outlive its terminated parent.");
                    Assert.AreEqual(perRound, Volatile.Read(ref runs),
                        $"round {round}: every child's action must run exactly once.");
                }
            }, 30000, "R3: concurrent DefineNested and Terminate deadlocked.");
        }

        // ------------------------------------------------------------------------------------------
        // R1: the AddAction / Terminate race has no losing side
        // ------------------------------------------------------------------------------------------

        [Test]
        public void ConcurrentAddActionAndTerminate_RunsEveryRegisteredActionExactlyOnce()
        {
            // Registrations that land before the flip run from the snapshot; registrations that land after
            // it run on the registering thread. Neither lost nor double-run.
            const int rounds = 100;
            const int adders = 4;
            const int perAdder = 50;

            RunWithTimeout(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    var definition = _root.Lifetime.DefineNested("race" + round);
                    var invocations = 0;

                    using (var barrier = new Barrier(adders + 1))
                    {
                        var workers = new Task[adders + 1];
                        for (var a = 0; a < adders; a++)
                        {
                            workers[a] = Task.Run(() =>
                            {
                                barrier.SignalAndWait();
                                for (var i = 0; i < perAdder; i++)
                                {
                                    definition.Lifetime.AddAction(() => Interlocked.Increment(ref invocations));
                                }
                            });
                        }

                        workers[adders] = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            definition.Terminate();
                        });

                        Task.WaitAll(workers);
                    }

                    Assert.AreEqual(adders * perAdder, Volatile.Read(ref invocations),
                        $"round {round}: every action handed over must run exactly once, whether it " +
                        "arrived before, during, or after termination.");
                }
            }, 30000);
        }

        [Test]
        public void ConcurrentTerminate_InvokesEveryActionExactlyOnce()
        {
            const int taskCount = 8;
            var definition = _root.Lifetime.DefineNested("concurrent-terminate");
            var calls = 0;
            definition.Lifetime.AddAction(() => Interlocked.Increment(ref calls));

            RunWithTimeout(() =>
            {
                using (var barrier = new Barrier(taskCount))
                {
                    var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        definition.Terminate();
                    })).ToArray();

                    Task.WaitAll(tasks);
                }
            }, 15000, "Concurrent Terminate must not hang.");

            Assert.AreEqual(1, Volatile.Read(ref calls), "Termination is idempotent under concurrency.");
            Assert.IsTrue(definition.IsTerminated);
        }

        // ------------------------------------------------------------------------------------------
        // R5: no shared mutable state between Lifetime instances
        // ------------------------------------------------------------------------------------------

        [Test]
        public void ConcurrentlyCreatedLifetimes_EachInvokeOnlyTheirOwnActionsExactlyOnce()
        {
            // The 1.x pool was popped under lock(_pool) and pushed under the instance lock - a real race
            // that could hand one List<Action> to two live lifetimes.
            const int taskCount = 8;
            const int perTask = 200;
            var failures = 0;

            RunWithTimeout(() =>
            {
                var tasks = Enumerable.Range(0, taskCount).Select(_ => Task.Run(() =>
                {
                    for (var i = 0; i < perTask; i++)
                    {
                        var definition = _root.Lifetime.DefineNested();
                        var calls = 0;
                        definition.Lifetime.AddAction(() => Interlocked.Increment(ref calls));
                        definition.Terminate();
                        if (Volatile.Read(ref calls) != 1)
                        {
                            Interlocked.Increment(ref failures);
                        }
                    }
                })).ToArray();

                Task.WaitAll(tasks);
            }, 30000, "Concurrent scope churn must not hang.");

            Assert.AreEqual(0, failures, "Every lifetime must invoke exactly its own single action.");
        }

        [Test]
        public void ConcurrentDefineAndTerminateOfNestedLifetimes_RunsEveryRegistrationExactlyOnce()
        {
            // Many threads hammering AddDefinition, the detach closure, AddBracket, duplicate registrations
            // and Terminate on a shared parent with NO external synchronisation - unlike the 1.x
            // "thread safety" test, which serialised everything behind its own lock and exercised no
            // concurrency at all.
            const int threads = 8;
            const int iterations = 250;

            var stressRoot = _root.Lifetime.DefineNested("stress-root");
            var registered = 0;
            var invoked = 0;
            var errors = new ConcurrentQueue<Exception>();

            RunWithTimeout(() =>
            {
                using (var barrier = new Barrier(threads))
                {
                    var workers = new Task[threads];
                    for (var t = 0; t < threads; t++)
                    {
                        workers[t] = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            for (var i = 0; i < iterations; i++)
                            {
                                try
                                {
                                    var child = stressRoot.Lifetime.DefineNested();
                                    Interlocked.Increment(ref registered);
                                    child.Lifetime.AddAction(() => Interlocked.Increment(ref invoked));

                                    var grandChild = child.Lifetime.DefineNested();
                                    Interlocked.Increment(ref registered);
                                    grandChild.Lifetime.AddBracket(
                                        () => { },
                                        () => Interlocked.Increment(ref invoked));

                                    // duplicate delegate registration under contention (R6)
                                    Action duplicate = () => Interlocked.Increment(ref invoked);
                                    Interlocked.Add(ref registered, 2);
                                    child.Lifetime.AddAction(duplicate);
                                    child.Lifetime.AddAction(duplicate);

                                    if ((i & 1) == 0)
                                    {
                                        child.Terminate();
                                    }
                                    else
                                    {
                                        grandChild.Terminate();
                                    }
                                }
                                catch (Exception exception)
                                {
                                    errors.Enqueue(exception);
                                }
                            }
                        });
                    }

                    Task.WaitAll(workers);
                }
            }, 30000, "R3: concurrent nested define/terminate deadlocked.");

            stressRoot.Terminate();

            CollectionAssert.IsEmpty(errors,
                "No exception may escape concurrent define/terminate: " +
                string.Join(" | ", errors.Select(e => e.GetType().Name + ": " + e.Message)));
            Assert.AreEqual(Volatile.Read(ref registered), Volatile.Read(ref invoked),
                "Every registration - including duplicates and brackets - must run exactly once.");
            Assert.IsTrue(stressRoot.IsTerminated);
        }

        [Test]
        public void ConcurrentTerminationOfADeeplyNestedChain_TerminatesEveryLevelExactlyOnce()
        {
            // Several threads terminate different depths of one chain at the same moment; the cascade must
            // converge with each level's teardown having run exactly once and no level left alive.
            const int rounds = 100;
            const int depth = 8;

            RunWithTimeout(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    var chain = new Lifetime.Definition[depth];
                    var runs = new int[depth];
                    chain[0] = _root.Lifetime.DefineNested("chain0");
                    for (var level = 1; level < depth; level++)
                    {
                        chain[level] = chain[level - 1].Lifetime.DefineNested("chain" + level);
                    }

                    for (var level = 0; level < depth; level++)
                    {
                        var captured = level;
                        chain[level].Lifetime.AddAction(() => Interlocked.Increment(ref runs[captured]));
                    }

                    using (var barrier = new Barrier(4))
                    {
                        var tasks = new[]
                        {
                            Task.Run(() => { barrier.SignalAndWait(); chain[0].Terminate(); }),
                            Task.Run(() => { barrier.SignalAndWait(); chain[depth / 2].Terminate(); }),
                            Task.Run(() => { barrier.SignalAndWait(); chain[depth - 1].Terminate(); }),
                            Task.Run(() => { barrier.SignalAndWait(); chain[depth - 2].Terminate(); })
                        };
                        Task.WaitAll(tasks);
                    }

                    for (var level = 0; level < depth; level++)
                    {
                        Assert.AreEqual(1, Volatile.Read(ref runs[level]),
                            $"round {round}, level {level}: teardown must run exactly once.");
                        Assert.IsTrue(chain[level].IsTerminated,
                            $"round {round}, level {level}: no level may survive its ancestor.");
                    }
                }
            }, 30000, "R3: concurrent termination of a nested chain deadlocked.");
        }

        [Test]
        public void ConcurrentIntersectionCreationAndSourceTermination_NeverLeavesALiveIntersectionOfADeadSource()
        {
            // Intersection wires one definition into several parents; racing that against a source
            // termination must never produce a live intersection whose source is already dead.
            const int rounds = 200;

            RunWithTimeout(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    var first = _root.Lifetime.DefineNested("i-first" + round);
                    var second = _root.Lifetime.DefineNested("i-second" + round);
                    Lifetime.Definition intersection = null;

                    using (var barrier = new Barrier(2))
                    {
                        var create = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            intersection = Lifetime.Intersection(first.Lifetime, second.Lifetime);
                        });
                        var kill = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            first.Terminate();
                        });

                        Task.WaitAll(create, kill);
                    }

                    _tracked.Add(intersection);

                    Assert.IsTrue(first.IsTerminated);
                    Assert.IsTrue(intersection.IsTerminated,
                        $"round {round}: an intersection must be terminated once any of its sources is " +
                        "dead, however the two operations interleave.");

                    second.Terminate();
                }
            }, 30000, "R3: concurrent Intersection/Terminate deadlocked.");
        }

        // ------------------------------------------------------------------------------------------
        // R9: the practical reason AsCancellationToken exists
        // ------------------------------------------------------------------------------------------

        [Test]
        public void AsCancellationToken_CancelsAnAwaitingOperationWhenTheLifetimeTerminates()
        {
            var definition = _root.Lifetime.DefineNested("cts-await");
            var token = definition.Lifetime.AsCancellationToken();
            var pending = Task.Delay(Timeout.Infinite, token);

            definition.Terminate();

            // Deliberately NOT pending.Wait(timeout): Task.Wait throws AggregateException on a cancelled
            // task, so the blind version of this test would have failed with an unhandled exception rather
            // than an assertion. Spin on IsCompleted instead, then assert the completion state.
            Assert.IsTrue(SpinWait.SpinUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(10)),
                "The awaited operation must complete once the lifetime ends.");
            Assert.IsTrue(pending.IsCanceled);
        }

        [Test]
        public void AsCancellationToken_OnATerminatedLifetime_DoesNotHangAnAwait()
        {
            // The regression guard for the 1.x bug: the registration was dropped, the token never cancelled,
            // and any await on it hung forever.
            var definition = _root.Lifetime.DefineNested("cts-await-dead");
            definition.Terminate();

            var token = definition.Lifetime.AsCancellationToken();
            var pending = Task.Delay(Timeout.Infinite, token);

            Assert.IsTrue(SpinWait.SpinUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(10)));
            Assert.IsTrue(pending.IsCanceled);
        }
    }
}
