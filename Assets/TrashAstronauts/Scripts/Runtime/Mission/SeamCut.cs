using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// How a plasma flame cuts along a seam (pure maths, shared by the cutter, previews and tests).
    ///
    /// The flame leaves the nozzle along the tip's forward and meets the metal a few centimetres ahead; the spot where
    /// it meets (<see cref="Contact"/>) deepens the cells of the seam under it. A cell needs about
    /// <see cref="CellBurnSeconds"/> under the middle of the spot to go through, so a steady pass at
    /// <see cref="CleanSpeed"/> or slower cuts clean in one go; faster than that it only scores the metal, and well
    /// faster it bites less and less (<see cref="SpeedEfficiency"/>). The flame must be close (full strength up to
    /// <see cref="FullStandoff"/>, nothing beyond <see cref="MaxStandoff"/>), roughly facing the metal, and on the
    /// line (within <see cref="FullLateral"/>, nothing beyond <see cref="MaxLateral"/>).
    /// </summary>
    public static class SeamCut
    {
        /// <summary>The spot cuts at full rate within this distance of its centre along the seam, fading to nothing at <see cref="SpotOuter"/>.</summary>
        public const float SpotInner = 0.018f, SpotOuter = 0.03f;
        /// <summary>Integral of the spot's weight along the seam (metres of full-rate flame).</summary>
        public const float SpotWidth = SpotInner * 2f + (SpotOuter - SpotInner);
        /// <summary>Seconds of full-rate flame that cut one cell through.</summary>
        public const float CellBurnSeconds = 0.12f;
        /// <summary>The fastest steady pass that still cuts clean in one go (0.40 m/s).</summary>
        public const float CleanSpeed = SpotWidth / CellBurnSeconds;
        /// <summary>Distance from nozzle to metal along the flame: full strength up to the first, none past the second.</summary>
        public const float FullStandoff = 0.05f, MaxStandoff = 0.09f;
        /// <summary>A nozzle this close to the face (or pushed into it) touches the metal whatever its angle.</summary>
        public const float TouchDistance = 0.015f, PushedThrough = 0.05f;
        /// <summary>Distance of the flame's spot from the seam line: full strength up to the first, none past the second.</summary>
        public const float FullLateral = 0.025f, MaxLateral = 0.05f;
        /// <summary>Cosine between flame and face normal: full strength from the first, none below the second (about 78 degrees off).</summary>
        public const float FullFacing = 0.6f, MinFacing = 0.2f;
        /// <summary>
        /// Heat taken by a flame biting cleanly into fresh metal, as a fraction of the heat of a flame in the air or on
        /// metal already cut (the work carries the heat away): 1 / this many times slower to overheat.
        /// </summary>
        public const float CleanCutHeatCapacity = 2.5f;

        /// <summary>Weight of the spot at a distance along the seam from its centre (1 in the middle, 0 at the edge).</summary>
        public static float SpotWeight(float offset)
        {
            float d = Mathf.Abs(offset);
            if (d <= SpotInner) return 1f;
            if (d >= SpotOuter) return 0f;
            float t = (d - SpotInner) / (SpotOuter - SpotInner);
            return 1f - t * t * (3f - 2f * t);
        }

        /// <summary>How much of its strength the flame puts into the metal at this speed along the seam (1 up to clean speed).</summary>
        public static float SpeedEfficiency(float speed)
        {
            if (speed <= CleanSpeed) return 1f;
            return Mathf.Clamp(1f - 0.6f * (speed / CleanSpeed - 1f), 0.25f, 1f);
        }

        /// <summary>Depth a single steady pass at this speed leaves in fresh metal (1 = through).</summary>
        public static float PassDepth(float speed)
        {
            if (speed <= 0.0001f) return 1f;
            return Mathf.Min(1f, SpotWidth / (speed * CellBurnSeconds) * SpeedEfficiency(speed));
        }

        public struct Contact
        {
            /// <summary>Where the flame meets the face (world).</summary>
            public Vector3 point;
            /// <summary>Metres from the middle of the stretch, along it.</summary>
            public float along;
            /// <summary>Metres from the seam line across the face (0 on the line), counting overshoot past its ends.</summary>
            public float offLine;
            /// <summary>Metres of flame between nozzle and metal.</summary>
            public float standoff;
            /// <summary>0..1 for distance, angle and how close to the line the spot is: the share of the flame that cuts.</summary>
            public float strength;
            /// <summary>The same without the line term (does the flame reach the metal at all).</summary>
            public float reach;
        }

        static float Smooth01(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Where a flame from <paramref name="nozzle"/> along <paramref name="flameDirection"/> meets this stretch's face,
        /// and how strongly it cuts there. False when it does not reach the face (too far, facing away, parallel).
        /// </summary>
        public static bool Evaluate(CutPoint stretch, Vector3 nozzle, Vector3 flameDirection, out Contact contact)
        {
            contact = default;
            if (stretch == null) return false;
            Vector3 origin = stretch.transform.position;
            Vector3 n = stretch.Normal;
            float side = Vector3.Dot(nozzle - origin, n);
            float facing = Vector3.Dot(flameDirection, n);
            Vector3 point;
            float standoff, facingStrength;
            if (Mathf.Abs(side) <= TouchDistance || (side < 0f && side >= -PushedThrough && facing < 0f))
            {
                // The nozzle is on the metal, or pushed into it from the front: it cuts where it is.
                point = nozzle - n * side;
                standoff = 0f;
                facingStrength = 1f;
            }
            else if (side * facing < 0f)
            {
                standoff = -side / facing;
                if (standoff > MaxStandoff) return false;
                point = nozzle + flameDirection * standoff;
                facingStrength = Smooth01(MinFacing, FullFacing, Mathf.Abs(facing));
            }
            else return false;

            Vector3 local = point - origin;
            Vector3 axis = stretch.Axis;
            float along = Vector3.Dot(local, axis);
            Vector3 across = local - axis * along - n * Vector3.Dot(local, n);
            float half = stretch.SeamLength * 0.5f;
            float overshoot = Mathf.Max(0f, Mathf.Abs(along) - half);
            float offLine = Mathf.Sqrt(across.sqrMagnitude + overshoot * overshoot);

            contact.point = point;
            contact.along = along;
            contact.offLine = offLine;
            contact.standoff = standoff;
            contact.reach = (1f - Smooth01(FullStandoff, MaxStandoff, standoff)) * facingStrength;
            contact.strength = contact.reach * (1f - Smooth01(FullLateral, MaxLateral, across.magnitude));
            return contact.reach > 0f;
        }
    }
}
