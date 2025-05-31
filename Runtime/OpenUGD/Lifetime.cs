using System;
using System.Collections.Generic;

namespace OpenUGD
{
    /// <summary>
    /// Represents a lifetime that can be used to manage the lifecycle of resources or actions.
    /// </summary>
    public class Lifetime
    {
        /// <summary>
        /// Represents a definition of a lifetime that can be used to create, manage nested lifetimes.
        /// </summary>
        public class Definition : IDisposable
        {
            private Definition(string id)
            {
                Id = id;
                Lifetime = new Lifetime();
                ParentId = -1;
            }

            /// <summary>
            /// [used for debugging purposes]
            /// Unique identifier for the lifetime definition, used for debugging purposes.
            /// </summary>
            public string Id { get; }

            /// <summary>
            /// Indicates whether the lifetime has been terminated.
            /// </summary>
            public bool IsTerminated => Lifetime.IsTerminated;

            /// <summary>
            /// The lifetime associated with this definition, which can be used to manage the lifecycle of resources or actions.
            /// </summary>
            public Lifetime Lifetime { get; }

            /// <summary>
            /// [used for debugging purposes]
            /// The ID of the parent lifetime, if any. This is used to track the hierarchy of lifetimes (used for debugging purposes).
            /// </summary>
            public int ParentId { get; private set; }

            /// <summary>
            /// Terminates the lifetime and invokes all registered actions.
            /// </summary>
            public void Terminate() => Lifetime.Terminate();

            /// <summary>
            /// Disposes the definition, which terminates the lifetime and invokes all registered actions.
            /// </summary>
            public void Dispose() => Lifetime.Terminate();

            /// <summary>
            /// Disposes the definition, which terminates the lifetime and invokes all registered actions.
            /// </summary>
            void IDisposable.Dispose() => Lifetime.Terminate();

            /// <summary>
            /// Defines a new lifetime definition with the specified lifetime and optional ID for debug.
            /// </summary>
            /// <param name="lifetime"></param>
            /// <param name="id"></param>
            /// <returns></returns>
            /// <exception cref="ArgumentNullException"></exception>
            /// <exception cref="InvalidOperationException"></exception>
            public static Definition Define(Lifetime lifetime, string id = null)
            {
                if (lifetime == null)
                    throw new ArgumentNullException(nameof(lifetime),
                        $"{nameof(lifetime)} can't be null on define new definition");
                if (lifetime._actions == null)
                    throw new InvalidOperationException(
                        $"{nameof(lifetime)} can't be terminated on define new definition");

                var definition = new Definition(id) {
                    ParentId = lifetime.Id
                };
                lifetime.AddDefinition(definition);
                return definition;
            }

            /// <summary>
            /// Creates a new lifetime definition that represents the intersection of multiple lifetimes.
            /// </summary>
            /// <param name="lifetimes"></param>
            /// <returns></returns>
            public static Definition Intersection(params Lifetime[] lifetimes)
            {
                var definition = Define(Eternal);
                foreach (var lifetime in lifetimes)
                {
                    lifetime.AddDefinition(definition);
                }

                return definition;
            }
        }

        private static readonly Stack<List<Action>> _pool = new Stack<List<Action>>();
        private static int _instances;

        /// <summary>
        /// Represents a lifetime that never terminates, allowing actions to be registered indefinitely.
        /// </summary>
        public static readonly Lifetime Eternal = new Lifetime();

        private List<Action> _actions;
        private readonly int _id;
        private readonly object _lock = new object();

        /// <summary>
        /// Defines a new lifetime definition with the specified lifetime and optional ID for debug.
        /// </summary>
        /// <param name="lifetime"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public static Definition Define(Lifetime lifetime, string id = null)
        {
            return Definition.Define(lifetime, id);
        }

        /// <summary>
        /// Creates a new lifetime definition that represents the intersection of multiple lifetimes.
        /// </summary>
        /// <param name="lifetimes"></param>
        /// <returns></returns>
        public static Definition Intersection(params Lifetime[] lifetimes)
        {
            return Definition.Intersection(lifetimes);
        }

        private Lifetime()
        {
            lock (_pool)
            {
                _id = ++_instances;
                _actions = _pool.Count != 0 ? _pool.Pop() : new List<Action>();
            }
        }

        /// <summary>
        /// [used for debugging purposes]
        /// Unique identifier for the lifetime instance, used for debugging purposes.
        /// </summary>
        public int Id => _id;

        /// <summary>
        /// Indicates whether the lifetime has been terminated.
        /// </summary>
        public bool IsTerminated {
            get {
                lock (_lock)
                {
                    return _actions == null;
                }
            }
        }

        /// <summary>
        /// Adds an action to be invoked when the lifetime is terminated.
        /// </summary>
        /// <param name="action"></param>
        /// <returns>Lifetime</returns>
        /// <exception cref="ArgumentException"></exception>
        public Lifetime AddAction(Action action)
        {
            lock (_lock)
            {
                if (_actions == null)
                {
                    return this;
                }

                if (_actions.Contains(action))
                {
                    throw new ArgumentException("Action already exist");
                }

                _actions.Add(action);
                return this;
            }
        }

        /// <summary>
        /// Adds an action to be invoked when the lifetime is terminated, and returns a disposable that will remove the action when disposed.
        /// </summary>
        /// <param name="onOpen">The onOpen action to be invoked if the lifetime is not terminated</param>
        /// <param name="onTerminate">The onTerminate action to be invoked when the lifetime is terminated</param>
        /// <returns>Lifetime</returns>
        public Lifetime AddBracket(Action onOpen, Action onTerminate)
        {
            lock (_lock)
            {
                if (_actions != null)
                {
                    AddAction(onTerminate);
                    onOpen?.Invoke();
                }

                return this;
            }
        }

        /// <summary>
        /// Defines a new nested lifetime definition with the current lifetime as its parent.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Definition DefineNested(string name = null)
        {
            return Define(this, name);
        }

        private void AddDefinition(Definition definition)
        {
            lock (_lock)
            {
                if (_actions == null)
                {
                    definition.Terminate();
                }
                else if (!_actions.Contains(definition.Terminate))
                {
                    _actions.Add(definition.Terminate);
                    definition.Lifetime.AddAction(() => {
                        lock (_lock)
                        {
                            if (_actions != null)
                            {
                                _actions.Remove(definition.Terminate);
                            }
                        }
                    });
                    if (IsTerminated || definition.IsTerminated)
                    {
                        definition.Terminate();
                    }
                }
            }
        }

        private void Terminate()
        {
            lock (_lock)
            {
                if (_actions == null)
                    return;

                var actions = _actions;
                _actions = null;
                for (var i = actions.Count - 1; i >= 0; i--)
                {
                    var action = actions[i];
                    action?.Invoke();
                }

                actions.Clear();
                _pool.Push(actions);
            }
        }
    }
}
