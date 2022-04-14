using System;
using System.Collections.Generic;

namespace OpenUGD
{
    public class Lifetime
    {
        public class Definition : IDisposable
        {
            private Definition(string id)
            {
                Id = id;
                Lifetime = new Lifetime();
                ParentId = -1;
            }

            public string Id { get; }
            public bool IsTerminated => Lifetime.IsTerminated;
            public Lifetime Lifetime { get; }
            public int ParentId { get; private set; }

            public void Terminate() => Lifetime.Terminate();

            public void Dispose() => Lifetime.Terminate();

            public static Definition Define(Lifetime lifetime, string id = null)
            {
                if (lifetime == null)
                    throw new ArgumentNullException(nameof(lifetime),
                        $"{nameof(lifetime)} can't be null on define new definition");
                if (lifetime._actions == null)
                    throw new InvalidOperationException(
                        $"{nameof(lifetime)} can't be terminated on define new definition");

                var definition = new Definition(id)
                {
                    ParentId = lifetime.Id
                };
                lifetime.AddDefinition(definition);
                return definition;
            }

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

        public static readonly Lifetime Eternal = new Lifetime();

        private List<Action> _actions;
        private readonly int _id;
        private readonly object _lock = new object();

        public static Definition Define(Lifetime lifetime, string id = null)
        {
            return Definition.Define(lifetime, id);
        }

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

        public int Id => _id;

        public bool IsTerminated
        {
            get
            {
                lock (_lock)
                {
                    return _actions == null;
                }
            }
        }

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
                    definition.Lifetime.AddAction(() =>
                    {
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