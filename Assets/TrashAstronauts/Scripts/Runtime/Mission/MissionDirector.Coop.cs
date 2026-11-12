using System;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Co-op stage 2, "shared contract, ride-along": the host runs a real contract for everyone and a
    /// client follows it. This half of the director is the client's mirror: every method here applies
    /// something the host already decided, never judges it, and can be applied twice without changing
    /// the result. The host's half is <see cref="CoopHostRules"/>; the numeric entry points the session
    /// calls are <see cref="CoopClientApply"/>. Nothing here runs offline (Net == null).
    /// </summary>
    public sealed partial class MissionDirector
    {
        // ------------------------------------------------------------------ run limits (host)

        /// <summary>
        /// While true, <see cref="BuildRunParams"/> leaves out the creature, the blackouts and the webs: the
        /// ride-along contract, which is all co-op supports until the creature is shared. Set by
        /// <see cref="CoopHostRules"/> for as long as it exists; false offline.
        /// </summary>
        public bool CoopRideAlong { get; set; }

        /// <summary>
        /// Co-op stage 4 switch (host): while true, a ride-along contract carries the creature again (any species,
        /// the Mimic included) and its blackouts; webs stay unstrung. False (the default) keeps the creature ashore. Only read
        /// while <see cref="CoopRideAlong"/> is set; <see cref="CoopHostRules.CreatureEnabled"/> sets it.
        /// </summary>
        public bool CoopCreatureEnabled { get; set; }

        void ApplyCoopRunLimits(ref RunStartParams run)
        {
            if (!CoopRideAlong) return;
            if (CoopCreatureEnabled)
            {
                run.wireArmedMask = 0u;
                return;
            }
            run.creatureIndex = RunStartParams.NoCreature;
            run.hazardFlags = 0;
            run.wireArmedMask = 0u;
        }

        // ------------------------------------------------------------------ presence (client)

        HazardPauseReason m_AwayReasons;

        /// <summary>
        /// A client's stand-in for <see cref="SetHazardPause"/>: the headset came off, lost tracking or lost
        /// focus. The local player becomes Away (not waited for, not counted for oxygen) and the host is told
        /// once per change. Hazards keep running: only the host's pause freezes them.
        /// </summary>
        public void SetLocalAway(HazardPauseReason reason, bool away)
        {
            bool was = m_AwayReasons != HazardPauseReason.None;
            if (away) m_AwayReasons |= reason;
            else m_AwayReasons &= ~reason;
            bool now = m_AwayReasons != HazardPauseReason.None;
            var local = LocalPlayer;
            if (local != null) local.IsAway = now;
            if (was != now && Net != null) Net.ReportPause(now);
        }

        /// <summary>The local player's own Away reasons (a client: headset off, tracking or focus lost).</summary>
        public HazardPauseReason AwayReasons => m_AwayReasons;

        /// <summary>
        /// Joining as a client: a headset already off, untracked or unfocused had that as a hazard pause; from
        /// now on it is Away (HazardPauseMonitor routes the matching "back" to <see cref="SetLocalAway"/>), so the
        /// reasons move over and the hazards are no longer paused here. The host's pause stays where it is.
        /// </summary>
        public void MovePausesToAway()
        {
            HazardPauseReason own = m_PauseReasons & ~HazardPauseReason.HostPaused;
            if (own == HazardPauseReason.None) return;
            bool wasPaused = HazardsPaused;
            m_PauseReasons &= HazardPauseReason.HostPaused;
            bool wasAway = m_AwayReasons != HazardPauseReason.None;
            m_AwayReasons |= own;
            var local = LocalPlayer;
            if (local != null) local.IsAway = true;
            if (wasPaused != HazardsPaused) HazardPauseChanged?.Invoke(HazardsPaused);
            if (!wasAway && Net != null) Net.ReportPause(true);
        }

        /// <summary>
        /// Leaving a session: Away reasons still in force become hazard pauses again (single-player pauses for a
        /// headset that is off), so the matching "back" from HazardPauseMonitor clears them. Nothing is stranded.
        /// </summary>
        void MoveAwayToPauses()
        {
            HazardPauseReason away = m_AwayReasons;
            m_AwayReasons = HazardPauseReason.None;
            var local = LocalPlayer;
            if (local != null) local.IsAway = false;
            if (away == HazardPauseReason.None) return;
            bool wasPaused = HazardsPaused;
            m_PauseReasons |= away;
            if (wasPaused != HazardsPaused) HazardPauseChanged?.Invoke(HazardsPaused);
        }

        /// <summary>
        /// After a session: every salvage item of the last run that is still in the scene (a results screen) is an
        /// ordinary local item again: simulated here, grabbable, in nobody else's hand, no replica, no item sync.
        /// A host keeps an item a crew member was holding or flying where it last was.
        /// </summary>
        void RestoreRunItemsLocal()
        {
            if (ItemClient != null) ItemClient.Dispose();
            ItemClient = null;
            for (int i = 0; i < m_RunItems.Count; i++)
            {
                var item = m_RunItems[i];
                if (item == null) continue;
                var replica = item.GetComponent<CoopItemReplica>();
                if (replica != null) Destroy(replica);
                var sync = item.GetComponent<ItemSync>();
                if (sync != null)
                {
                    sync.End();
                    Destroy(sync);
                }
                item.SetRemoteHolder(PlayerId.None);
                item.SetHostOnly(false);
                item.SetSimulatedHere(true);
            }
        }

        // ------------------------------------------------------------------ items in everyone's hands (stage 3)

        /// <summary>
        /// A client's item sync (co-op stage 3): while set, the run's items can be grabbed here, carried, thrown
        /// and handed over (each through its ItemSync). Set by <see cref="CoopItemClient"/> for its session; null
        /// offline, on a host, and in a session that does not share grabbing (its items stay host-only).
        /// </summary>
        public CoopItemClient ItemClient { get; set; }

        static ItemSync SyncOf(SalvageItem item)
        {
            return item != null && item.TryGetComponent(out ItemSync sync) && sync.Active ? sync : null;
        }

        /// <summary>Forgets every Away reason of the local player (leaving a session).</summary>
        public void ClearLocalAway()
        {
            m_AwayReasons = HazardPauseReason.None;
            var local = LocalPlayer;
            if (local != null) local.IsAway = false;
        }

        /// <summary>The host's hazards were paused or resumed (HostPause, or the Clock's flag).</summary>
        public void ApplyHostPause(bool paused) => SetHazardPause(HazardPauseReason.HostPaused, paused);

        /// <summary>True while nothing has been heard from the host for a while (HOST PAUSED). Freezes the local clock.</summary>
        public bool HostSilent { get; private set; }

        public void ApplyHostSilent(bool silent) => HostSilent = silent;

        // ------------------------------------------------------------------ run (client)

        /// <summary>Raised on a client when the host turned down its extraction request: bit n = slot n is not aboard.</summary>
        public event Action<int> ExtractRefused;
        /// <summary>Who was missing the last time an extraction request was turned down (bit n = slot n).</summary>
        public int LastExtractRefusedMask { get; private set; }

        public void ApplyExtractRefused(int missingSlotsMask)
        {
            LastExtractRefusedMask = missingSlotsMask;
            ExtractRefused?.Invoke(missingSlotsMask);
        }

        /// <summary>
        /// The host started a contract: begin exactly that one (<see cref="BeginRun"/>, so salvage ids come
        /// from the host's run number and match on every machine). On a client every item then follows the
        /// host: kinematic, moved only by the poses that arrive, until a local hand takes it (with the
        /// session's <see cref="ItemClient"/>; without one it cannot be grabbed here). A repeat of the run
        /// already in progress does nothing; parameters that fail their own plan hash are refused.
        /// </summary>
        public bool ApplyRunStart(in RunStartParams run)
        {
            if (run.planHash != run.ComputePlanHash())
            {
                Debug.LogError("[Mission] Run parameters from the host failed their plan hash; contract not started.");
                return false;
            }
            if (Session.Phase == MissionPhase.Active)
            {
                var current = CurrentRun;
                if (current.planHash == run.planHash && current.runIndex == run.runIndex && current.seed == run.seed) return true;
                // A different contract while one is running here: the end of the old one never arrived.
                AbortRun();
            }
            if (!BeginRun(in run)) return false;

            if (Net != null && Net.IsClient)
            {
                bool shared = ItemClient != null && !ItemClient.Disposed;
                for (int i = 0; i < m_RunItems.Count; i++)
                {
                    var item = m_RunItems[i];
                    if (item == null) continue;
                    item.SetSimulatedHere(false);
                    item.SetHostOnly(!shared);
                    CoopItemReplica.For(item).ResetForRun();
                }
                if (shared) ItemClient.BeginRun();
            }
            m_ClockAge = 0f;
            m_HaveClock = true;
            return true;
        }

        // ------------------------------------------------------------------ clock (client)

        /// <summary>How far past the host's last Clock a client keeps counting on its own.</summary>
        public const float MaxClockExtrapolation = 1.5f;
        bool m_HaveClock;
        float m_ClockAge;

        /// <summary>
        /// The host's mission time and shared tank. Between two of these (2 a second) the client counts on
        /// with the host's own rule, for at most <see cref="MaxClockExtrapolation"/>; it never ends the run.
        /// </summary>
        public bool ApplyClock(float elapsedSeconds, float oxygenRemainingSeconds)
        {
            if (!Session.ApplyClock(elapsedSeconds, oxygenRemainingSeconds)) return false;
            m_ClockAge = 0f;
            m_HaveClock = true;
            return true;
        }

        void ExtrapolateClientClock(bool everyoneSafe, float dt)
        {
            if (!m_HaveClock || Session.Phase != MissionPhase.Active || HazardsPaused || HostSilent || dt <= 0f) return;
            float step = Mathf.Min(dt, MaxClockExtrapolation - m_ClockAge);
            if (step <= 0f) return;
            m_ClockAge += step;
            float drain = everyoneSafe ? 0f : step * (1f + m_Config.exertionOxygenCost * CrewExertion());
            Session.ApplyClock(Session.ElapsedSeconds + step, Session.Oxygen.Remaining - drain);
        }

        float CrewExertion()
        {
            float exertion = 0f;
            foreach (var player in m_Players)
            {
                if (player == null || player.IsAway || player.IsDowned) continue;
                if (player.IsRemote) exertion = Mathf.Max(exertion, player.RemoteExertion);
                else if (player.locomotion != null && player.locomotion.zeroG != null)
                    exertion = Mathf.Max(exertion, player.locomotion.zeroG.Exertion);
            }
            return exertion;
        }

        // ------------------------------------------------------------------ salvage (client)

        /// <summary>The host booked the first pickup of an item.</summary>
        public bool ApplyItemCollected(SalvageItem item, PlayerId by)
        {
            return item != null && Session.ApplyRemoteCollect(item.SalvageId, by);
        }

        /// <summary>The host's word on what an item is worth now.</summary>
        public bool ApplyItemValue(SalvageItem item, int value)
        {
            if (item == null) return false;
            if (!item.IsSecured) item.SetRemoteValue(value);
            return Session.ApplyRemoteValue(item.SalvageId, value);
        }

        /// <summary>
        /// The host's locker took the item: let go of it here if a hand had it, book it in the mirror, and
        /// lock it into the same slot the host used. Returns true the first time.
        /// </summary>
        public bool ApplyItemDeposited(SalvageItem item, PlayerId by, int finalValue, int trickBonus, int lockerSlot)
        {
            if (item == null) return false;
            CancelLocalSelection(item);
            var sync = SyncOf(item);
            if (sync != null) sync.OnDeposited();
            bool booked = Session.ApplyRemoteDeposit(item.SalvageId, by, finalValue, trickBonus);
            if (!item.IsSecured)
            {
                item.SetRemoteValue(finalValue);
                if (locker != null) locker.ApplyAccepted(item, trickBonus, lockerSlot);
            }
            if (booked) SalvageDeposited?.Invoke(item);
            return booked;
        }

        static void CancelLocalSelection(SalvageItem item)
        {
            var grab = item.grab;
            if (grab == null || !grab.isSelected || grab.interactionManager == null) return;
            item.NetSuppressRelease = true;
            grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
        }

        /// <summary>The host brought a lost item back to this position.</summary>
        public bool ApplyItemRecovered(SalvageItem item, Vector3 position)
        {
            if (item == null || item.IsSecured || item.IsMounted) return false;
            if (item.SimulatedHere)
            {
                item.RecoverTo(position);
                return true;
            }
            CoopItemReplica.For(item).Snap(position, item.transform.rotation);
            return true;
        }

        /// <summary>
        /// A pose of a host-simulated item. It is shown about 100 ms behind (see <see cref="CoopItemReplica"/>);
        /// a pose from an older ownership epoch is ignored. <paramref name="localTime"/> is when it was sampled,
        /// on this machine's unscaled clock (NaN = now).
        /// </summary>
        public bool ApplyItemPose(SalvageItem item, Vector3 position, Quaternion rotation, int epoch, bool held, bool atRest,
            double localTime = double.NaN)
        {
            if (item == null || item.SimulatedHere) return false;
            var sync = SyncOf(item);
            if (sync != null && !sync.AcceptPose((byte)epoch)) return false;
            return CoopItemReplica.For(item).Push(position, rotation, epoch, held, atRest, localTime);
        }

        /// <summary>A host-simulated item settled at this pose (heals a copy that missed the last poses).</summary>
        public bool ApplyItemRest(SalvageItem item, int epoch, Vector3 position, Quaternion rotation)
        {
            if (item == null || item.SimulatedHere) return false;
            var sync = SyncOf(item);
            if (sync != null && !sync.AcceptRest((byte)epoch)) return false;
            return CoopItemReplica.For(item).Rest(position, rotation, epoch);
        }

        // ------------------------------------------------------------------ world (client)

        public bool ApplyDoorState(SlidingDoor door, bool open)
        {
            if (door == null) return false;
            bool changed = door.IsOpen != open;
            door.ApplyReplicated(open);
            return changed;
        }

        public bool ApplyPointCut(Cuttable panel, int index, PlayerId by)
        {
            return panel != null && panel.ApplyRemotePointCut(index, by);
        }

        /// <summary>The host opened a panel; its plate leaves with the push the host rolled.</summary>
        public bool ApplyPanelOpened(Cuttable panel, PlayerId by, Vector3 plateImpulse, Vector3 plateTorque)
        {
            if (panel == null) return false;
            bool booked = !panel.isTraining && Session.TryOpenPanel(panel.panelId, by);
            bool opened = !panel.IsOpen;
            panel.ApplyOpened(plateImpulse, plateTorque);
            if (opened && !panel.isTraining) PanelOpened?.Invoke(panel);
            return booked || opened;
        }

        /// <summary>The host's latest door, panel, weld and web state. Only ever moves things toward it.</summary>
        public void ApplyWorldState(in WorldStateMsg state)
        {
            var ids = NetIds;
            for (int i = 0; i < ids.Doors.Count && i < 16; i++)
            {
                var door = DoorAt(i);
                if (door != null) ApplyDoorState(door, (state.doorOpenMask & (1 << i)) != 0);
            }
            for (int i = 0; i < ids.Panels.Count; i++)
            {
                var panel = PanelAt(i);
                if (panel == null) continue;
                if (i < 16 && (state.panelOpenMask & (1 << i)) != 0)
                {
                    if (!panel.IsOpen || (!panel.isTraining && !Session.IsPanelOpen(panel.panelId)))
                        ApplyPanelOpened(panel, PlayerId.None, Vector3.zero, Vector3.zero);
                    continue;
                }
                int offset = ids.CutPointOffset(i);
                for (int k = 0; k < panel.TotalCuts; k++)
                {
                    if (state.IsWeldDone(offset + k)) panel.ApplyRemotePointCut(k, PlayerId.None);
                }
            }
            for (int i = 0; i < ids.Wires.Count && i < 32; i++)
            {
                var wire = WireAt(i);
                if (wire == null || !wire.Armed) continue;
                if ((state.wireBurntMask & (1u << i)) != 0) wire.ApplyState(WireChange.Burnt, wire.transform.position, PlayerId.None);
                else if ((state.wireSnappedMask & (1u << i)) != 0) wire.ApplyState(WireChange.Snapped, wire.transform.position, PlayerId.None);
            }
        }

        // ------------------------------------------------------------------ end of the run (client)

        /// <summary>
        /// The host ended the contract with this result. The first time: the mirror takes the host's figures,
        /// this headset's own save is paid once, the result sting plays, the creature stops, the hatch closes,
        /// and the local player is brought home behind a fade if the run failed or they are outside the tug.
        /// Returns the stored result, or null when there was no run to end (a repeat changes nothing).
        /// </summary>
        public MissionResult ApplyRunEnded(MissionResult result)
        {
            if (result == null) return null;
            var stored = Session.ApplyRemoteEnd(result);
            if (stored == null) return null;
            // This headset's own share, less this player's revive penalties (the host's figures, RunEnded).
            Session.BankInto(Profile, LocalPayoutSlot);
            ClearCrewDowned();
            m_HaveClock = false;

            if (noise != null) noise.Muted = true;
            EndCreatures();
            if (Sfx.Library != null)
            {
                AudioClip sting = stored.IsFailure ? Sfx.Library.resultFail
                    : stored.Outcome == MissionOutcome.ContractComplete ? Sfx.Library.resultSuccess
                    : Sfx.Library.resultPartial;
                Sfx.PlayUi(sting, 0.8f);
            }
            BringLocalPlayerHome(stored.IsFailure);
            MissionEnded?.Invoke(stored);
            return stored;
        }

        /// <summary>The host left the results screen.</summary>
        public bool ApplyReturnedToHub() => ReturnToHub();

        /// <summary>
        /// Drops the contract with no result and goes back to the hub (the link to the host was lost, or the
        /// player left the session mid-contract). Nothing is banked; the local player is brought home if
        /// they are outside the tug. Returns false if no contract was running.
        /// </summary>
        public bool AbortRun()
        {
            if (!Session.AbortToHub()) return false;
            m_HaveClock = false;
            HostSilent = false;
            SetHazardPause(HazardPauseReason.HostPaused, false);
            ClearRunObjects();
            if (locker != null) locker.ResetForRun();
            if (noise != null) noise.Muted = true;
            EndCreatures();
            if (blackout != null) blackout.EndBlackout(true);
            ClearCrewDowned();
            BringLocalPlayerHome(false);
            return true;
        }

        void EndCreatures()
        {
            if (enemies == null) return;
            foreach (var enemy in enemies)
            {
                if (enemy != null) enemy.EndRun();
            }
        }

        /// <summary>Fade-teleports the local rig to the hub spawn (always, or only if its head is outside the tug), then seals the hatch.</summary>
        void BringLocalPlayerHome(bool always)
        {
            var local = LocalPlayer;
            bool outside = local != null && safeZone != null && !safeZone.Contains(local.HeadPosition);
            // Onto this player's own crew spot (slot 0 and single-player: the hub spawn), not on top of the host.
            if (local != null && local.locomotion != null && hubSpawn != null && (always || outside))
                local.locomotion.FadeTeleport(local.fader, HubSpotFor(local), hubSpawn.rotation, CloseHatch);
            else
                CloseHatch();
        }
    }
}
