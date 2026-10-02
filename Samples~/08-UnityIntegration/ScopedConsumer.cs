using UnityEngine;

namespace OpenUGD.Samples.UnityIntegration
{
    /// <summary>
    /// A consumer of a <see cref="LifetimeScope"/>, showing the two Unity scopes you usually want:
    /// "while this object exists" and "while this component is enabled".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object scope comes from the <see cref="LifetimeScope"/> component. The enabled scope is a
    /// nested definition created in <c>OnEnable</c> and terminated in <c>OnDisable</c> — which is exactly
    /// the acquire/release pair that <c>OnEnable</c>/<c>OnDisable</c> are for, except that everything
    /// registered inside it is released automatically, wherever in the file it was registered.
    /// </para>
    /// <para>
    /// Note that the enabled scope is nested under the object scope, so destroying the GameObject
    /// terminates it too, even though <c>OnDisable</c> and <c>OnDestroy</c> ordering is not something you
    /// then have to think about: whichever runs first, the clean-up runs exactly once.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(LifetimeScope))]
    public class ScopedConsumer : MonoBehaviour
    {
        private LifetimeScope _scope;
        private Lifetime.Definition _enabledScope;

        private void Awake()
        {
            _scope = GetComponent<LifetimeScope>();

            // Registered on the OBJECT scope: this runs once, when the GameObject is destroyed.
            _scope.Lifetime.AddAction(() => Debug.Log($"{name}: object scope ended"));
        }

        private void OnEnable()
        {
            // A child scope for "while enabled". DefineNested throws if the parent has already
            // terminated, which cannot happen here: a destroyed object does not get OnEnable.
            _enabledScope = _scope.Lifetime.DefineNested(name + ":enabled");

            // Acquire/release as one expression (sample 04). The release half is not written anywhere
            // else in this file, and cannot be forgotten when this block is edited.
            _enabledScope.Lifetime.AddBracket(
                onOpen: () => Debug.Log($"{name}: subscribed"),
                onTerminate: () => Debug.Log($"{name}: unsubscribed")
            );
        }

        private void OnDisable()
        {
            // Ends the enabled scope and everything registered in it. Idempotent, so a second call — or a
            // cascade from the object scope terminating first — is harmless.
            _enabledScope?.Terminate();
            _enabledScope = null;
        }
    }
}
