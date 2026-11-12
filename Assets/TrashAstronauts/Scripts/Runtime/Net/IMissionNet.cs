using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The only way the game talks to a co-op session. <see cref="MissionDirector.Net"/> is null in
    /// single-player and every call site tests that first, so offline none of this exists.
    ///
    /// The game calls the Request/Report/Forward methods on a CLIENT, at the exact places where offline
    /// (or on the host) it would change shared state itself: the gate is always
    /// <c>if (Net != null &amp;&amp; Net.IsClient) { Net.RequestX(...); return; }</c>. The host hears
    /// about its own changes through the game's existing events (MissionStarted, MissionEnded,
    /// SalvageDamaged, CollectionLocker.Accepted, SlidingDoor.Changed, Cuttable.Opened,
    /// WebTripwire.Changed, ...), not
    /// through this interface.
    /// </summary>
    public interface IMissionNet
    {
        /// <summary>True on a machine that joined someone else's session. Shared state arrives from the host.</summary>
        bool IsClient { get; }
        /// <summary>True on the machine that owns the session's shared state.</summary>
        bool IsHost { get; }
        /// <summary>The local player's slot in the crew (0 for the host). Equals the local rig's PlayerId.Value.</summary>
        int LocalSlot { get; }

        // ---- contract
        /// <summary>The local player pressed START CONTRACT.</summary>
        void RequestStart();
        /// <summary>The local player confirmed extraction.</summary>
        void RequestExtract();

        // ---- world
        /// <summary>The local player pressed a door button. Carries the state they want, never a toggle.</summary>
        void RequestDoor(SlidingDoor door, bool desiredOpen);
        /// <summary>The local player's cutter finished one cut point (index in <see cref="Cuttable.cutPoints"/>).</summary>
        void ReportPointCut(Cuttable panel, int pointIndex);

        // ---- salvage
        /// <summary>An item this machine is simulating took a knock and should now be worth <paramref name="newValue"/>.</summary>
        void ReportSalvageDamage(SalvageItem item, int newValue);

        // ---- creature and hazards
        /// <summary>The local player blundered through a web, or burnt it away.</summary>
        void ReportWire(WebTripwire wire, WireChange change);
        /// <summary>The local player's tranquilizer dart hit the creature.</summary>
        void ReportTranqHit(Vector3 origin, Vector3 hitPoint);
        /// <summary>A gameplay noise made by the local player, for the creature on the host to hear.</summary>
        void ForwardNoise(in NoiseEvent noise);

        // ---- presence
        /// <summary>The local headset was taken off, lost tracking or the app was paused (or the reverse).</summary>
        void ReportPause(bool paused);
        /// <summary>The local player changed a setting the crew can see.</summary>
        void ReportSettingsChanged(Handedness handedness);
    }
}
