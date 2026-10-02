using System;
using System.Collections.Generic;

namespace OpenUGD.Samples.BoundedOperation
{
    /// <summary>
    /// The everyday shape: <c>using var scope = lifetime.DefineNested("...")</c> for an operation with a
    /// beginning and an end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Lifetime.Definition"/> implements <see cref="IDisposable"/>, so <c>using</c> and
    /// <c>using var</c> just work. The closing brace of the enclosing block ends the scope and runs every
    /// clean-up registered inside it, LIFO — including any clean-up registered by code you called and never
    /// looked inside.
    /// </para>
    /// <para>
    /// <b>The implicit conversion.</b> <c>Definition</c> converts implicitly to <c>Lifetime</c> (it is
    /// null-safe: a null Definition converts to a null Lifetime). So a method that takes a
    /// <see cref="Lifetime"/> can be handed the <c>scope</c> variable directly — no <c>.Lifetime</c> at the
    /// call site. That is not just sugar: the conversion is one-way, so passing <c>scope</c> to a helper
    /// <b>downgrades</b> it from "can terminate" to "can only observe and register". The capability split
    /// of sample 01 is enforced automatically at every call boundary.
    /// </para>
    /// <para>
    /// The conversion applies to arguments, assignments and return values only. C# never applies a
    /// user-defined conversion to the receiver of a member or extension-method call, so registering on the
    /// scope itself still reads <c>scope.Lifetime.AddAction(...)</c>, as below.
    /// </para>
    /// <para>
    /// Caveat worth knowing: because <c>Dispose()</c> is <c>Terminate()</c>, a throwing clean-up surfaces as
    /// an <see cref="AggregateException"/> thrown <i>at the closing brace</i>. See sample 02.
    /// </para>
    /// </remarks>
    public static class BoundedOperationSample
    {
        public static void Run(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            var app = Lifetime.Eternal.DefineNested("app");
            try
            {
                LoadLevel(app.Lifetime, "forest", log);
                log("back from LoadLevel — everything it allocated is already released");

                LoadLevel(app.Lifetime, "cave", log);
                log("back from LoadLevel again");
            }
            finally
            {
                app.Terminate();
            }
        }

        /// <summary>
        /// A bounded operation. Note that it takes a <see cref="Lifetime"/>: the caller keeps the authority
        /// to end the app, and this method can only nest under it.
        /// </summary>
        private static void LoadLevel(Lifetime lifetime, string level, Action<string> log)
        {
            // One line to open the scope. The closing brace of this method closes it.
            using var scope = lifetime.DefineNested("load-level:" + level);

            log($"[{level}] loading");

            // Register clean-up directly on the scope's lifetime...
            scope.Lifetime.AddAction(() => log($"[{level}] released loader"));

            // ...and hand the scope to helpers. `scope` is a Definition, but these parameters are
            // Lifetime, so the implicit conversion runs and the helpers receive no way to end the scope.
            var atlas = LoadAtlas(scope, level, log);
            Subscribe(scope, level, log);

            log($"[{level}] loaded {atlas.Count} textures");

            // No explicit Terminate() anywhere: leaving the method — by return, by break, or by an
            // exception — runs every registration above, in reverse order.
        }

        /// <summary>
        /// A helper that allocates something and ties its release to the scope it was given.
        /// It cannot end that scope; it can only add to it.
        /// </summary>
        private static List<string> LoadAtlas(Lifetime lifetime, string level, Action<string> log)
        {
            var atlas = new List<string> { level + "/ground", level + "/props" };
            lifetime.AddAction(() =>
            {
                atlas.Clear();
                log($"[{level}] released atlas");
            });
            return atlas;
        }

        /// <summary>
        /// The unsubscribe half never has to be written at a second call site: it lives right next to the
        /// subscribe half, and the scope decides when it runs.
        /// </summary>
        private static void Subscribe(Lifetime lifetime, string level, Action<string> log)
        {
            log($"[{level}] subscribed to events");
            lifetime.AddAction(() => log($"[{level}] unsubscribed from events"));
        }
    }
}
