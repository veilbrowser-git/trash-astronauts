using System;
using UnityEngine;

namespace TrashAstronauts.Core
{
    public enum EnemyState
    {
        /// <summary>Docked and harmless (hub, tutorial, results, or still waking up).</summary>
        Dormant = 0,
        Patrol = 1,
        Investigate = 2,
        Chase = 3,
        Search = 4,
    }

    /// <summary>What the drone's body sensed this frame. Filled in by the scene-side EnemyDrone.</summary>
    public struct EnemyPercept
    {
        public float deltaTime;
        public bool canSeePlayer;
        public bool playerInSafeZone;
        public Vector3 playerPosition;
        /// <summary>True when the body has arrived at <see cref="EnemyBrain.PointOfInterest"/> (or as close as it can get).</summary>
        public bool atPointOfInterest;
    }

    /// <summary>
    /// The drone's decision making: patrol -> investigate noise -> chase on sight -> search -> back to patrol.
    /// Pure logic with no scene access, so it is unit tested and would run unchanged on a co-op host.
    /// The body (EnemyDrone) handles movement, line of sight and audio.
    /// </summary>
    public sealed class EnemyBrain
    {
        readonly EnemyTuning m_Tuning;
        float m_LingerTimer;
        float m_LostSightTimer;

        public EnemyState State { get; private set; } = EnemyState.Dormant;
        /// <summary>Where the drone wants to be in Investigate, Chase and Search.</summary>
        public Vector3 PointOfInterest { get; private set; }
        /// <summary>0..1 "I think I see something" meter. Reaching 1 starts a chase.</summary>
        public float Suspicion { get; private set; }
        public float TimeInState { get; private set; }

        public event Action<EnemyState, EnemyState> StateChanged;

        public EnemyBrain(EnemyTuning tuning) { m_Tuning = tuning ?? new EnemyTuning(); }

        public float SpeedForState
        {
            get
            {
                switch (State)
                {
                    case EnemyState.Patrol: return m_Tuning.patrolSpeed;
                    case EnemyState.Investigate: return m_Tuning.investigateSpeed;
                    case EnemyState.Chase: return m_Tuning.chaseSpeed;
                    case EnemyState.Search: return m_Tuning.searchSpeed;
                    default: return 0f;
                }
            }
        }

        public void Sleep()
        {
            Suspicion = 0f;
            SetState(EnemyState.Dormant);
        }

        public void WakeUp()
        {
            Suspicion = 0f;
            SetState(EnemyState.Patrol);
        }

        /// <summary>Feeds a noise to the drone. Returns true if it decided to go and look.</summary>
        public bool HearNoise(in NoiseEvent noise, Vector3 selfPosition)
        {
            if (State == EnemyState.Dormant) return false;
            if (State == EnemyState.Chase)
            {
                // While it can see its quarry nothing distracts it. Once it has lost sight, any sound it
                // can hear tells it where to go next (which is also how a thrown can saves you).
                if (m_LostSightTimer < 0.35f) return false;
                if (!NoiseRules.IsAudible(noise, selfPosition, m_Tuning.hearingRadiusAtFullLoudness)) return false;
                PointOfInterest = noise.Position;
                m_LostSightTimer = Mathf.Max(0.35f, m_LostSightTimer - 2f);
                return true;
            }
            if (!NoiseRules.IsAudible(noise, selfPosition, m_Tuning.hearingRadiusAtFullLoudness)) return false;

            PointOfInterest = noise.Position;
            m_LingerTimer = 0f;
            if (State == EnemyState.Investigate) TimeInState = 0f;
            else SetState(EnemyState.Investigate);
            return true;
        }

        /// <summary>Seconds since it last saw its quarry during a chase.</summary>
        public float LostSightSeconds => State == EnemyState.Chase ? m_LostSightTimer : 0f;

        /// <summary>Points a hunt (investigate, chase without sight, search) at a better guess.</summary>
        public void Redirect(Vector3 point)
        {
            if (State == EnemyState.Dormant || State == EnemyState.Patrol) return;
            PointOfInterest = point;
        }

        public void Tick(in EnemyPercept percept)
        {
            if (State == EnemyState.Dormant) return;

            float dt = Mathf.Max(0f, percept.deltaTime);
            TimeInState += dt;
            bool sees = percept.canSeePlayer && !percept.playerInSafeZone;

            switch (State)
            {
                case EnemyState.Patrol:
                    if (UpdateSuspicion(sees, dt, 1f)) StartChase(percept.playerPosition);
                    break;

                case EnemyState.Investigate:
                    if (UpdateSuspicion(sees, dt, 2f)) { StartChase(percept.playerPosition); break; }
                    if (percept.atPointOfInterest)
                    {
                        m_LingerTimer += dt;
                        if (m_LingerTimer >= m_Tuning.investigateLingerSeconds) SetState(EnemyState.Patrol);
                    }
                    break;

                case EnemyState.Chase:
                    if (percept.playerInSafeZone)
                    {
                        // The tug is off limits: give up immediately and poke around where the trail went cold.
                        StartSearch();
                        break;
                    }
                    if (sees)
                    {
                        PointOfInterest = percept.playerPosition;
                        m_LostSightTimer = 0f;
                    }
                    else
                    {
                        m_LostSightTimer += dt;
                        if (m_LostSightTimer >= m_Tuning.chaseLoseSightSeconds) StartSearch();
                    }
                    break;

                case EnemyState.Search:
                    if (UpdateSuspicion(sees, dt, 2.5f)) { StartChase(percept.playerPosition); break; }
                    if (percept.atPointOfInterest) m_LingerTimer += dt;
                    if (m_LingerTimer >= m_Tuning.searchSeconds || TimeInState >= m_Tuning.searchSeconds * 3f)
                        SetState(EnemyState.Patrol);
                    break;
            }
        }

        bool UpdateSuspicion(bool sees, float dt, float rateMultiplier)
        {
            float acquire = Mathf.Max(0.05f, m_Tuning.sightAcquireSeconds);
            if (sees) Suspicion = Mathf.Min(1f, Suspicion + dt * rateMultiplier / acquire);
            else Suspicion = Mathf.Max(0f, Suspicion - dt / (acquire * 2f));
            return Suspicion >= 1f;
        }

        void StartChase(Vector3 playerPosition)
        {
            PointOfInterest = playerPosition;
            m_LostSightTimer = 0f;
            SetState(EnemyState.Chase);
        }

        void StartSearch()
        {
            m_LingerTimer = 0f;
            Suspicion = 0.5f;
            SetState(EnemyState.Search);
        }

        void SetState(EnemyState next)
        {
            if (State == next) return;
            var previous = State;
            State = next;
            TimeInState = 0f;
            if (next == EnemyState.Patrol || next == EnemyState.Dormant) m_LingerTimer = 0f;
            if (next == EnemyState.Patrol) Suspicion = Mathf.Min(Suspicion, 0.25f);
            StateChanged?.Invoke(previous, next);
        }
    }
}
