using System;
using System.Reflection;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Whether a player is downed (immobile, waiting to be revived), asked by features that must leave a downed player
    /// alone (crew shoves) without a compile-time dependency on the revive feature.
    ///
    /// <see cref="Check"/> is the hook: the revive feature (or the merge that brings it in) sets it, for example to
    /// <c>p =&gt; p.IsDowned</c>. Until then a public bool property or field named IsDowned on PlayerContext is found
    /// once by reflection and read; without either, nobody is ever downed.
    /// </summary>
    public static class CrewDowned
    {
        /// <summary>Set by the revive feature. Null: look for PlayerContext.IsDowned, else false.</summary>
        public static Func<PlayerContext, bool> Check;

        static bool s_Looked;
        static PropertyInfo s_Property;
        static FieldInfo s_Field;

        public static bool Is(PlayerContext player)
        {
            if (player == null) return false;
            if (Check != null) return Check(player);
            if (!s_Looked)
            {
                s_Looked = true;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                var property = typeof(PlayerContext).GetProperty("IsDowned", flags);
                if (property != null && property.PropertyType == typeof(bool) && property.CanRead) s_Property = property;
                var field = typeof(PlayerContext).GetField("IsDowned", flags);
                if (field != null && field.FieldType == typeof(bool)) s_Field = field;
            }
            if (s_Property != null) return (bool)s_Property.GetValue(player);
            if (s_Field != null) return (bool)s_Field.GetValue(player);
            return false;
        }
    }
}
