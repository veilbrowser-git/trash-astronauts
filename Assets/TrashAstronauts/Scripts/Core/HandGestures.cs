using UnityEngine;

namespace TrashAstronauts.Core
{
    /// <summary>
    /// Turns tracked hand joint positions into the two inputs the game needs: how closed the fist is
    /// (grip) and how pinched thumb and index are (trigger). Pure maths so it can be unit tested.
    /// Distances are in metres and assume an adult hand; the ranges are deliberately forgiving.
    /// </summary>
    public static class HandGestures
    {
        public const float OpenTipToPalm = 0.105f;
        public const float FistTipToPalm = 0.055f;
        public const float PinchOpen = 0.06f;
        public const float PinchClosed = 0.018f;

        /// <summary>0 = open hand, 1 = fist. Uses the middle, ring and little fingertips.</summary>
        public static float FistAmount(Vector3 palm, Vector3 middleTip, Vector3 ringTip, Vector3 littleTip)
        {
            float average = (Vector3.Distance(palm, middleTip) + Vector3.Distance(palm, ringTip) + Vector3.Distance(palm, littleTip)) / 3f;
            return Mathf.Clamp01(Mathf.InverseLerp(OpenTipToPalm, FistTipToPalm, average));
        }

        /// <summary>0 = thumb and index apart, 1 = touching.</summary>
        public static float PinchAmount(Vector3 thumbTip, Vector3 indexTip)
        {
            return Mathf.Clamp01(Mathf.InverseLerp(PinchOpen, PinchClosed, Vector3.Distance(thumbTip, indexTip)));
        }

        /// <summary>Button with hysteresis so a value hovering near the threshold does not flicker.</summary>
        public static bool Latch(bool wasPressed, float value, float press = 0.7f, float release = 0.4f)
        {
            return wasPressed ? value > release : value >= press;
        }
    }
}
