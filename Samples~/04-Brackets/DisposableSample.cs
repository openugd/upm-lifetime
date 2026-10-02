using System;

namespace OpenUGD.Samples.Brackets
{
    /// <summary>
    /// <c>disposable.With(lifetime)</c> — the shorthand for the most common bracket of all: "this
    /// <see cref="IDisposable"/> belongs to this scope".
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns the disposable, typed as whatever you passed in, so it chains onto the construction
    /// expression and the ownership decision sits on the same line as the allocation. That is the point:
    /// you cannot construct the thing and forget to say who owns it, because saying so is part of the same
    /// statement.
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
    /// Do not call <c>With</c> on a <see cref="Lifetime.Definition"/> to tie one scope to another: it
    /// compiles, but it neither nests the definition nor detaches when it ends first. Create the scope with
    /// <c>other.DefineNested()</c> or <c>Lifetime.Intersection(...)</c> instead.
    /// </para>
    /// </remarks>
    public static class DisposableSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            log("-- ownership stated on the line of construction --");
            using (var scope = Lifetime.Eternal.DefineNested("with"))
            {
                // Fire and forget: nobody needs the handle back, only its disposal.
                new Handle("connection", log).With(scope.Lifetime);

                // Construct, own and use: With returns the Handle it was given, not an IDisposable.
                var buffer = new Handle("buffer", log).With(scope.Lifetime);
                buffer.Use();
            }
            // Both handles were disposed here, LIFO: buffer first, then connection.

            log("-- a disposable handed to a dead scope is disposed immediately --");
            var dead = Lifetime.Eternal.DefineNested("dead");
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
