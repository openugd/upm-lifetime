using System;

namespace OpenUGD.Samples.Brackets
{
    /// <summary>
    /// <c>disposable.With(lifetime)</c> — the shorthand for the most common bracket of all: "this
    /// <see cref="IDisposable"/> belongs to this scope".
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns the disposable, so it chains onto the construction expression and the ownership decision
    /// sits on the same line as the allocation. That is the point: you cannot construct the thing and
    /// forget to say who owns it, because saying so is part of the same statement.
    /// </para>
    /// <para>
    /// <b>Dispose is called exactly once per call.</b> Registering one instance on two lifetimes disposes
    /// it twice, so either register it once or use a type whose <c>Dispose</c> is idempotent.
    /// </para>
    /// <para>
    /// <b>On an already-terminated lifetime the object is disposed immediately</b>, before <c>With</c>
    /// returns. Unlike a bracket, the object here already exists — the caller just constructed it — so
    /// dropping the registration would leak it. <i>(Before 2.0.0 the registration was silently dropped and
    /// the object was never disposed at all.)</i>
    /// </para>
    /// <para>
    /// Note the declared return type is <see cref="IDisposable"/>, not the concrete type, so keep the
    /// typed variable and chain <c>With</c> as a separate statement when you need the concrete type.
    /// </para>
    /// </remarks>
    public static class DisposableSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            log("-- ownership stated on the line of construction --");
            using (var scope = Lifetime.Define(Lifetime.Eternal, "with"))
            {
                // The chained form, when you do not need the concrete type back.
                new Handle("connection", log).With(scope.Lifetime);

                // The typed form: keep your variable, then say who owns it.
                var buffer = new Handle("buffer", log);
                buffer.With(scope.Lifetime);
                buffer.Use();
            }
            // Both handles were disposed here, LIFO: buffer first, then connection.

            log("-- a disposable handed to a dead scope is disposed immediately --");
            var dead = Lifetime.Define(Lifetime.Eternal, "dead");
            dead.Terminate();

            new Handle("late-arrival", log).With(dead.Lifetime);
            log("  ...disposed inside the With() call above, not leaked");
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

            public void Use() => _log($"  {_name}: used");

            public void Dispose() => _log($"  {_name}: disposed");
        }
    }
}
