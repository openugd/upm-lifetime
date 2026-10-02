using UnityEngine;

namespace OpenUGD.Samples.UnityIntegration
{
    /// <summary>
    /// Binds a <see cref="Lifetime"/> to the life of a <see cref="GameObject"/>: the scope opens when the
    /// object awakes and ends in <see cref="OnDestroy"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component is the <b>owner</b> (see sample 01): the <see cref="Lifetime.Definition"/> is a
    /// private field, and the public surface is a <see cref="Lifetime"/> property. Other components can
    /// register clean-up on it; none of them can destroy the scope out from under the GameObject.
    /// </para>
    /// <para>
    /// Nest one of these under another (child object under parent object) and the scopes nest with them,
    /// because <c>OnDestroy</c> already cascades down the transform hierarchy.
    /// </para>
    /// <para>
    /// <b>The package itself has no engine dependency</b> — <c>Runtime</c> compiles with
    /// <c>noEngineReferences</c> and references neither <c>UnityEngine</c> nor <c>UnityEditor</c>. This
    /// sample is the adapter, and it is 30 lines. There is deliberately nothing Unity-specific inside the
    /// library.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public class LifetimeScope : MonoBehaviour
    {
        private Lifetime.Definition _definition;

        /// <summary>
        /// The scope of this GameObject. Handing this out hands out no authority to end it.
        /// </summary>
        public Lifetime Lifetime
        {
            get
            {
                EnsureDefined();
                return _definition.Lifetime;
            }
        }

        private void Awake() => EnsureDefined();

        private void OnDestroy()
        {
            // Terminate() rethrows a clean-up action's exception — an AggregateException if several threw
            // (see sample 02). Letting it escape OnDestroy is fine — Unity logs it and, crucially, every
            // other clean-up action has already run by then. Catch it here only if you want it logged your
            // own way.
            _definition?.Terminate();
        }

        private void EnsureDefined()
        {
            // Lazy, because another component's Awake may reach for this scope before ours has run.
            // DefineNested is cheap, and Awake's call then finds it already there.
            //
            // The type is spelled out because this class has a property of the same name; inside a method
            // of this type, `OpenUGD.Lifetime` is the unambiguous way to name the type.
            _definition ??= OpenUGD.Lifetime.Eternal.DefineNested(name);
        }
    }
}
