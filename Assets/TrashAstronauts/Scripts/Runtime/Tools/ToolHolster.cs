using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// A holster on the player's body. Gripping the holster always puts its tool in that hand, whether
    /// the tool is sitting in the holster or drifting somewhere across the room. That makes it the
    /// reliable recall for essential tools.
    /// </summary>
    public sealed class ToolHolster : MonoBehaviour
    {
        public SuitTool tool;
        public Transform socket;
        public XRSimpleInteractable interactable;
        [Tooltip("Outline shown while the tool is away so the empty holster is still easy to find.")]
        public GameObject emptyMarker;

        public Vector3 SocketPosition => socket != null ? socket.position : transform.position;

        void OnEnable()
        {
            if (interactable != null) interactable.selectEntered.AddListener(OnSelected);
        }

        void OnDisable()
        {
            if (interactable != null) interactable.selectEntered.RemoveListener(OnSelected);
        }

        void Update()
        {
            if (emptyMarker != null && tool != null)
            {
                bool empty = !tool.IsDocked;
                if (emptyMarker.activeSelf != empty) emptyMarker.SetActive(empty);
            }
        }

        void OnSelected(SelectEnterEventArgs args)
        {
            if (tool == null || tool.IsHeld || !isActiveAndEnabled) return;
            StartCoroutine(HandOver(args.interactorObject));
        }

        IEnumerator HandOver(IXRSelectInteractor interactor)
        {
            // Wait a frame so we are outside XRI's interaction update when we swap the selection.
            yield return null;
            if (tool == null || tool.IsHeld || interactor == null) yield break;

            var manager = interactable.interactionManager;
            if (manager == null) yield break;
            // Check the grip before swapping: once the holster is deselected the interactor has no
            // selection, and XRI only reports select as active on the press frame or while holding something.
            bool stillGripping = interactable.IsSelected(interactor) && interactor.isSelectActive;
            if (interactable.IsSelected(interactor)) manager.SelectExit(interactor, (IXRSelectInteractable)interactable);
            if (!stillGripping) yield break; // let go already

            Transform attach = interactor.GetAttachTransform(tool.grab);
            tool.PrepareHandOver(attach.position, attach.rotation);
            manager.SelectEnter(interactor, (IXRSelectInteractable)tool.grab);
        }
    }
}
