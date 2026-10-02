using System;
using System.Threading;

namespace OpenUGD
{
    /// <summary>
    /// Extension methods over <see cref="Lifetime"/>. The core type stays small on purpose; everything that
    /// is not scope machinery lives here, and this surface is deliberately kept minimal.
    /// </summary>
    public static class LifetimeExtensions
    {
        /// <summary>
        /// Ties an <see cref="IDisposable"/> to a scope: it is disposed when the lifetime terminates.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>If the lifetime is already terminated the disposable is disposed immediately</b>, before this
        /// method returns; the (already disposed) instance is still returned so the call stays usable inside
        /// an expression. This follows directly from the <see cref="Lifetime.AddAction"/> invariant. An
        /// exception thrown by that immediate <see cref="IDisposable.Dispose"/> propagates to the caller.
        /// <i>Changed in 2.0.0</i> — the registration used to be silently dropped, so the object was never
        /// disposed at all.
        /// </para>
        /// <para>
        /// <see cref="IDisposable.Dispose"/> is called exactly once per call to this method. Registering one
        /// instance on two lifetimes disposes it twice, so either register it once or use a type whose
        /// <c>Dispose</c> is idempotent.
        /// </para>
        /// <para>
        /// <b>Returns the static type it was given</b>, so construction, ownership and use fit in one
        /// declaration: <c>var stream = File.OpenRead(path).With(lifetime);</c> gives a
        /// <c>FileStream</c>. <i>Changed in 2.0.0</i> — the return type used to be <see cref="IDisposable"/>.
        /// For a value type <typeparamref name="T"/>, the lifetime disposes a boxed copy taken at this call,
        /// not the value returned to you; prefer reference types here.
        /// </para>
        /// <para>
        /// <b>Not for a <see cref="Lifetime.Definition"/>.</b> <c>definition.With(other)</c> compiles,
        /// because a definition is an <see cref="IDisposable"/>, and the definition does end when
        /// <c>other</c> ends — but through a plain action, not as a nested scope, so if it ends first nothing
        /// detaches it and it stays referenced by <c>other</c> until <c>other</c> ends. Create the scope
        /// with <c>other.DefineNested()</c>, or with
        /// <see cref="Lifetime.Intersection"/> when it must end with either of two lifetimes; both detach
        /// when the scope ends first.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The static type of the resource; returned unchanged.</typeparam>
        /// <param name="disposable">The resource to tie to the scope.</param>
        /// <param name="lifetime">The lifetime that owns it.</param>
        /// <returns>
        /// <paramref name="disposable"/>, so the call can be chained onto a construction expression.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="lifetime"/> or <paramref name="disposable"/> is <c>null</c>.
        /// </exception>
        public static T With<T>(this T disposable, Lifetime lifetime) where T : IDisposable
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime), "Lifetime cannot be null.");
            }

            if (disposable == null)
            {
                throw new ArgumentNullException(nameof(disposable), "Disposable cannot be null.");
            }

            lifetime.AddAction(disposable.Dispose);
            return disposable;
        }

        /// <summary>
        /// Bridges a lifetime into the <see cref="CancellationToken"/> world: returns a token that is
        /// cancelled when the lifetime terminates, and that is <i>already</i> cancelled if the lifetime has
        /// already terminated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <i>Changed in 2.0.0</i> — on a terminated lifetime this used to return a token that could never
        /// cancel, so any <c>await</c> on it hung forever. The already-terminated case now short-circuits to
        /// a pre-cancelled token; it allocates nothing and registers nothing. If the lifetime terminates
        /// during the call instead, <see cref="Lifetime.AddAction"/> invokes <c>Cancel</c> immediately, so
        /// the returned token is cancelled either way — the race cannot produce a token that never cancels.
        /// </para>
        /// <para>
        /// <b>Ownership: the source is deliberately never disposed.</b> Two reasons, and the second is the
        /// decisive one:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// Disposing it would break callers. The token is handed out and this method cannot know who still
        /// holds it. After <c>Dispose</c>, <c>token.Register(...)</c> and <c>token.WaitHandle</c> throw
        /// <see cref="ObjectDisposedException"/> — and a token is normally inspected precisely <i>after</i>
        /// it has been cancelled. Disposal is only safe for whoever owns the source exclusively, and here
        /// that is nobody.
        /// </description></item>
        /// <item><description>
        /// It is not a leak. <see cref="CancellationTokenSource"/> has no finalizer and holds no unmanaged
        /// resource unless someone materialises <c>CancellationToken.WaitHandle</c> or sets a timer, and
        /// this method does neither. Once the lifetime has terminated and the caller has dropped the token,
        /// the source is unreachable and the GC reclaims it.
        /// </description></item>
        /// </list>
        /// <para>
        /// <b>What this does cost:</b> each call on a live lifetime allocates one source and adds one entry
        /// to that lifetime's action list, held until termination. Call it once and keep the token, or scope
        /// it to a nested definition — never call it in a loop against a long-lived lifetime, least of all
        /// <see cref="Lifetime.Eternal"/>, which never terminates.
        /// </para>
        /// </remarks>
        /// <param name="lifetime">The lifetime to observe.</param>
        /// <returns>A token that is cancelled when <paramref name="lifetime"/> terminates.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="lifetime"/> is <c>null</c>.</exception>
        public static CancellationToken AsCancellationToken(this Lifetime lifetime)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime), "Lifetime cannot be null.");
            }

            if (lifetime.IsTerminated)
            {
                return new CancellationToken(true);
            }

            var tokenSource = new CancellationTokenSource();
            lifetime.AddAction(tokenSource.Cancel);
            return tokenSource.Token;
        }

        /// <summary>
        /// The positive reading of <see cref="Lifetime.IsTerminated"/>, for call sites where a negation
        /// would read badly.
        /// </summary>
        /// <remarks>
        /// A racy observation in concurrent code: a <c>true</c> result may be stale by the time you act on
        /// it. Prefer registering on the lifetime over testing it.
        /// </remarks>
        /// <param name="lifetime">The lifetime to test.</param>
        /// <returns><c>true</c> while the lifetime has not terminated.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="lifetime"/> is <c>null</c>.</exception>
        public static bool IsAlive(this Lifetime lifetime)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime), "Lifetime cannot be null.");
            }

            return !lifetime.IsTerminated;
        }

        /// <summary>
        /// Guard clause for operations that are meaningless after the scope has ended.
        /// </summary>
        /// <remarks>
        /// A racy check in concurrent code: passing it does not guarantee the lifetime is still alive on the
        /// next line. Use it to catch programming errors, not to synchronise.
        /// </remarks>
        /// <param name="lifetime">The lifetime to check.</param>
        /// <exception cref="ObjectDisposedException"><paramref name="lifetime"/> has been terminated.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="lifetime"/> is <c>null</c>.</exception>
        public static void ThrowIfTerminated(this Lifetime lifetime)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime), "Lifetime cannot be null.");
            }

            if (lifetime.IsTerminated)
            {
                throw new ObjectDisposedException(nameof(Lifetime), "Lifetime has been terminated.");
            }
        }
    }
}
