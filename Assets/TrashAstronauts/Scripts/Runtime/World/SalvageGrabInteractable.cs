using TrashAstronauts.Mission;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.World
{
    /// <summary>
    /// The grab on every loose, throwable thing (Builder.MakeGrabbable). Identical to XRGrabInteractable for
    /// anything that is not salvage, and for salvage that is simulated on this machine and in nobody else's
    /// hand, which is always the case offline. In co-op it adds two things:
    ///  - A copy that another machine simulates (a kinematic replica) becomes this machine's to simulate the
    ///    moment a local hand takes it, BEFORE the base grab runs. XRI remembers the body's kinematic state as
    ///    it grabs and puts it back as it lets go; doing it first means it remembers "dynamic", so the release
    ///    keeps the throw velocity whatever order XRI drops and detaches in. The switch goes through
    ///    SalvageItem.SetSimulatedHere, so RefreshBodyMode stays the only writer of isKinematic.
    ///  - Nothing can be selected while a crew member on another machine holds it (taking it out of someone's
    ///    hand is not a thing: they let go, you catch).
    /// </summary>
    public class SalvageGrabInteractable : XRGrabInteractable
    {
        SalvageItem m_Item;
        bool m_LookedForItem;

        /// <summary>The salvage this grab belongs to, or null for junk.</summary>
        public SalvageItem Item
        {
            get
            {
                if (!m_LookedForItem)
                {
                    m_Item = GetComponent<SalvageItem>();
                    m_LookedForItem = true;
                }
                return m_Item;
            }
        }

        /// <summary>How many local grabs turned a replica into a body simulated here (diagnostics and tests).</summary>
        public int ReplicaGrabs { get; private set; }

        public override bool IsSelectableBy(IXRSelectInteractor interactor)
        {
            var item = Item;
            if (item != null && item.RemoteHolder.IsValid) return false;
            return base.IsSelectableBy(interactor);
        }

        protected override void OnSelectEntering(SelectEnterEventArgs args)
        {
            var item = Item;
            if (item != null && !isSelected && !item.SimulatedHere && !item.IsSecured && !item.IsMounted)
            {
                // Ours to simulate from this frame on; the session hears of it from ItemSync right after.
                item.SetSimulatedHere(true);
                ReplicaGrabs++;
            }
            base.OnSelectEntering(args);
        }
    }
}
