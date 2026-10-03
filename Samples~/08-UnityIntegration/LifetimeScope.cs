using System;
using UnityEngine;

namespace OpenUGD.Samples.UnityIntegration
{
    /// <summary>
    /// Binds a <see cref="Lifetime"/> to the life of a <see cref="GameObject"/>: the scope opens when the
    /// object is first activated and ends in <see cref="OnDestroy"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component is the <b>owner</b> (see sample 01): the <see cref="Lifetime.Definition"/> is a
    /// private field, and the public surface is a <see cref="Lifetime"/> property. Other components can
    /// register clean-up on it; none of them can destroy the scope out from under the GameObject.
    /// </para>
    /// <para>
    /// <b>Inactive objects.</b> Unity calls <c>OnDestroy</c> only on a GameObject that has been active, so a
    /// scope created for one that never is would never end: it would stay nested in
    /// <see cref="OpenUGD.Lifetime.Eternal"/> for the rest of the process, with everything registered on it.
    /// The scope is therefore created in <c>Awake</c> — or earlier in the same activation, when another
    /// component's <c>Awake</c> or <c>OnEnable</c> asks for it before this one's <c>Awake</c> has run — and
    /// reading <see cref="Lifetime"/> on an object that is not active throws instead of creating one.
    /// </para>
    /// <para>
    /// <b>Scopes do not nest with the transform hierarchy.</b> Every scope here is nested directly in
    /// <see cref="OpenUGD.Lifetime.Eternal"/>, so a child object's scope is a sibling of its parent's, not a
    /// child. Destroying a parent object still ends both, because Unity destroys the children with it and
    /// each one runs its own <c>OnDestroy</c> — in Unity's order, not as one LIFO cascade. To really nest,
    /// look the parent's scope up in <c>Awake</c> (<c>transform.parent.GetComponentInParent&lt;LifetimeScope&gt;()</c>)
    /// and create this one with <c>DefineNested</c> on it; the price is that re-parenting the object later
    /// does not move its scope, so destroying the old parent would end the scope of an object that is still
    /// alive.
    /// </para>
    /// <para>
    /// <b>Domain reload.</b> <see cref="OpenUGD.Lifetime.Eternal"/> is static. With Enter Play Mode Options
    /// set to skip the domain reload it survives from one play session to the next, together with every
    /// scope still nested in it. That is harmless here only because every scope this component creates ends
    /// in <c>OnDestroy</c>, which Unity runs for every object it destroys when Play Mode ends. In a project,
    /// nest these scopes in a play-session root that is terminated when Play Mode ends instead of in
    /// <see cref="OpenUGD.Lifetime.Eternal"/> (see "Eternal and play sessions" in the package README).
    /// </para>
    /// <para>
    /// <b>The package itself has no engine dependency</b> — <c>Runtime</c> compiles with
    /// <c>noEngineReferences</c> and references neither <c>UnityEngine</c> nor <c>UnityEditor</c>. This
    /// sample is the adapter. There is deliberately nothing Unity-specific inside the library.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public class LifetimeScope : MonoBehaviour
    {
        private Lifetime.Definition _definition;

        /// <summary>
        /// The scope of this GameObject. Handing this out hands out no authority to end it. After the object
        /// is destroyed this is the scope it had, already terminated.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The GameObject has never been active, so Unity would never call <c>OnDestroy</c> to end a scope.
        /// Activate it first, or read this from <c>Start</c> or later.
        /// </exception>
        public Lifetime Lifetime => (_definition ?? Define()).Lifetime;

        private void Awake()
        {
            if (_definition == null) Define();
        }

        private void OnDestroy()
        {
            // Terminate() rethrows a clean-up action's exception — an AggregateException if several threw
            // (see sample 02). Letting it escape OnDestroy is fine — Unity logs it and, crucially, every
            // other clean-up action has already run by then. Catch it here only if you want it logged your
            // own way.
            _definition?.Terminate();
        }

        private Lifetime.Definition Define()
        {
            // Active in the hierarchy means this component's Awake has run or runs later in this same
            // activation, so OnDestroy will end what is created here. `this == null` is Unity's test for
            // a destroyed object (one destroyed before it was ever active).
            if (this == null || !gameObject.activeInHierarchy)
                throw new InvalidOperationException(
                    $"{nameof(LifetimeScope)}.{nameof(Lifetime)} was read on a GameObject that has never been " +
                    "active. Unity calls OnDestroy only on objects that have been active, so nothing would ever " +
                    "end its scope. Activate the object first, or read the scope from Start or later.");

            // The type is spelled out because this class has a property of the same name; inside a method
            // of this type, `OpenUGD.Lifetime` is the unambiguous way to name the type.
            _definition = OpenUGD.Lifetime.Eternal.DefineNested(name);
            return _definition;
        }
    }
}
