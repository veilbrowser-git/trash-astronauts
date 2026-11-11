using System;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// The one channel gameplay noise travels through. Tools and physics impacts emit, the drone listens.
    /// In co-op this is where a client's noise would be forwarded to the host.
    /// </summary>
    public sealed class NoiseSystem : MonoBehaviour
    {
        public event Action<NoiseEvent> NoiseEmitted;

        public int EmittedCount { get; private set; }
        public NoiseEvent LastNoise { get; private set; }
        public float LastNoiseTime { get; private set; } = -100f;
        /// <summary>When muted (no contract running) noises are dropped.</summary>
        public bool Muted { get; set; } = true;

        public void Emit(in NoiseEvent noise)
        {
            if (Muted || noise.Loudness <= 0f) return;
            EmittedCount++;
            LastNoise = noise;
            LastNoiseTime = Time.time;
            NoiseEmitted?.Invoke(noise);
        }

        void OnDrawGizmos()
        {
            if (!Application.isPlaying || Time.time - LastNoiseTime > 1.5f) return;
            Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.6f);
            Gizmos.DrawWireSphere(LastNoise.Position, LastNoise.Loudness * 2f);
        }
    }
}
