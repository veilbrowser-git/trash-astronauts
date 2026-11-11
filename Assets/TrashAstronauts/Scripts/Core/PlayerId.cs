using System;

namespace TrashAstronauts.Core
{
    /// <summary>
    /// Identifies which player caused a shared-state change (collect, deposit, cut, extract).
    /// Single-player only ever uses <see cref="Local"/>. In co-op this becomes the network client id,
    /// which is why mission rules take a PlayerId instead of looking up "the" player.
    /// </summary>
    [Serializable]
    public readonly struct PlayerId : IEquatable<PlayerId>
    {
        public readonly int Value;

        public PlayerId(int value) { Value = value; }

        public static readonly PlayerId None = new PlayerId(-1);
        public static readonly PlayerId Local = new PlayerId(0);

        public bool IsValid => Value >= 0;

        public bool Equals(PlayerId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PlayerId other && Equals(other);
        public override int GetHashCode() => Value;
        public static bool operator ==(PlayerId a, PlayerId b) => a.Value == b.Value;
        public static bool operator !=(PlayerId a, PlayerId b) => a.Value != b.Value;
        public override string ToString() => Value < 0 ? "Player(None)" : "Player(" + Value + ")";
    }
}
