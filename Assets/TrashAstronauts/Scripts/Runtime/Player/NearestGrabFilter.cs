using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Orders what a hand's direct grab touches so the first (the one a squeeze takes) is the thing nearest
    /// the palm: by the distance from the centre of the grab volume to the nearest point of each target's
    /// colliders, ties going to the target whose middle is closer. XRI's own order measures to each
    /// collider's pivot, which lets a big crate whose pivot happens to be nearer win over the small thing
    /// actually in the palm. Allocation free: the lists involved hold a handful of entries.
    /// </summary>
    public sealed class NearestGrabFilter : XRBaseTargetFilter
    {
        [Tooltip("The grab volume. Its centre (in the interactor's attach space, as XRI places it) is the point distances are measured from.")]
        public SphereCollider grabVolume;

        readonly List<float> m_Keys = new List<float>(8);

        /// <summary>The point the distances are measured from, for this interactor.</summary>
        public Vector3 PalmPoint(IXRInteractor interactor)
        {
            Transform attach = interactor.GetAttachTransform(null);
            if (attach == null) attach = interactor.transform;
            return grabVolume != null ? attach.TransformPoint(grabVolume.center) : attach.position;
        }

        public override void Process(IXRInteractor interactor, List<IXRInteractable> targets, List<IXRInteractable> results)
        {
            results.Clear();
            m_Keys.Clear();
            Vector3 palm = PalmPoint(interactor);
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                float key = Key(target, interactor, palm);
                // Insertion sort: a hand rarely touches more than two or three things at once.
                int at = m_Keys.Count;
                while (at > 0 && m_Keys[at - 1] > key) at--;
                m_Keys.Insert(at, key);
                results.Insert(at, target);
            }
        }

        /// <summary>Distance from <paramref name="palm"/> to the nearest surface of the target, plus a tenth of the distance to its middle.</summary>
        public static float Key(IXRInteractable target, IXRInteractor interactor, Vector3 palm)
        {
            if (target is XRBaseInteractable based && based.colliders.Count > 0)
            {
                float best = float.MaxValue;
                for (int c = 0; c < based.colliders.Count; c++)
                {
                    var collider = based.colliders[c];
                    if (collider == null || !collider.enabled) continue;
                    Vector3 nearest = ClosestPoint(collider, palm);
                    float key = Vector3.Distance(nearest, palm) + 0.1f * Vector3.Distance(collider.bounds.center, palm);
                    if (key < best) best = key;
                }
                if (best < float.MaxValue) return best;
            }
            return Mathf.Sqrt(target.GetDistanceSqrToInteractor(interactor));
        }

        /// <summary>Nearest point of a collider to a point (bounds for shapes PhysX cannot answer exactly).</summary>
        public static Vector3 ClosestPoint(Collider collider, Vector3 point)
        {
            if (collider is MeshCollider mesh && !mesh.convex) return collider.ClosestPointOnBounds(point);
            return collider.ClosestPoint(point);
        }
    }
}
