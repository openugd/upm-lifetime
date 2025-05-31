using System;
using System.Threading;

namespace OpenUGD
{
    /// <summary>
    /// Provides extension methods for the Lifetime class to manage resources and actions associated with a lifetime.
    /// </summary>
    public static class LifetimeExtensions
    {
        /// <summary>
        /// Disposes the given IDisposable when the lifetime is terminated.
        /// </summary>
        /// <param name="disposable"></param>
        /// <param name="lifetime"></param>
        /// <exception cref="ArgumentNullException">The lifetime or disposable cannot be null.</exception>
        /// <returns></returns>
        public static IDisposable With(this IDisposable disposable, Lifetime lifetime)
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
        /// Creates a CancellationToken that is cancelled when the lifetime is terminated.
        /// </summary>
        /// <param name="lifetime"></param>
        /// <exception cref="ArgumentNullException">The lifetime cannot be null.</exception>
        /// <returns>CancellationToken</returns>
        public static CancellationToken AsCancellationToken(this Lifetime lifetime)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime), "Lifetime cannot be null.");
            }

            var tokenSource = new CancellationTokenSource();
            lifetime.AddAction(tokenSource.Cancel);
            return tokenSource.Token;
        }

        /// <summary>
        /// Checks if the lifetime is still alive (not terminated).
        /// </summary>
        /// <param name="lifetime"></param>
        /// <exception cref="ArgumentNullException">The lifetime cannot be null.</exception>
        /// <returns></returns>
        public static bool IsAlive(this Lifetime lifetime)
        {
            if (lifetime == null)
            {
                throw new ArgumentNullException(nameof(lifetime), "Lifetime cannot be null.");
            }

            return !lifetime.IsTerminated;
        }

        /// <summary>
        /// Throws an exception if the lifetime has been terminated.
        /// </summary>
        /// <param name="lifetime"></param>
        /// <exception cref="ObjectDisposedException">The lifetime has been terminated.</exception>
        /// <exception cref="ArgumentNullException">The lifetime cannot be null.</exception>
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
