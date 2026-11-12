using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// The suit's carry pockets. Touch INV on the wrist display and a see-through tray of slots appears in
    /// front of you. Let go of a small piece of salvage on a slot and it is stowed there, shrunk, safe
    /// from knocks; grab it out again when you reach the locker. Close the tray and it all travels with
    /// you. Big things (the reactor core) do not fit. Nothing stowed counts until it is in the locker.
    /// </summary>
    public sealed class Inventory : MonoBehaviour
    {
        public PlayerContext player;
        [Tooltip("The see-through tray. Switched off when the inventory is closed.")]
        public GameObject panel;
        public Transform[] slots;
        public TMP_Text header;
        [Tooltip("The INV button on the wrist display.")]
        public PushButton wristButton;
        public float distance = 0.48f;
        public float drop = 0.3f;
        public float autoCloseDistance = 1.7f;
        [Tooltip("How close to a slot a released item must be to drop into it.")]
        public float storeRadius = 0.13f;
        [Tooltip("The longest side of a stowed item, in metres. Things are shrunk to fit.")]
        public float storedSize = 0.11f;

        SalvageItem[] m_Items;
        Vector3[] m_Scales;
        MissionDirector m_Director;

        public bool IsOpen => panel != null && panel.activeSelf;
        public int Capacity => slots != null ? slots.Length : 0;
        public int Count
        {
            get
            {
                int n = 0;
                if (m_Items != null) foreach (var item in m_Items) if (item != null) n++;
                return n;
            }
        }
        /// <summary>What everything stowed is worth right now.</summary>
        public int CarriedValue
        {
            get
            {
                int v = 0;
                if (m_Items != null) foreach (var item in m_Items) if (item != null) v += item.Value;
                return v;
            }
        }
        public SalvageItem ItemAt(int slot) => m_Items != null && slot >= 0 && slot < m_Items.Length ? m_Items[slot] : null;

        void Awake()
        {
            m_Items = new SalvageItem[Capacity];
            m_Scales = new Vector3[Capacity];
        }

        void OnEnable()
        {
            if (wristButton != null) wristButton.Pressed += OnWristButton;
        }

        void OnDisable()
        {
            if (wristButton != null) wristButton.Pressed -= OnWristButton;
            Unhook();
        }

        void Start()
        {
            if (panel != null) panel.SetActive(false);
            Refresh();
        }

        void Hook()
        {
            var director = MissionDirector.Active;
            if (director == m_Director) return;
            Unhook();
            m_Director = director;
            if (m_Director == null) return;
            m_Director.MissionStarted += Forget;
            m_Director.MissionEnded += OnMissionEnded;
        }

        void Unhook()
        {
            if (m_Director == null) return;
            m_Director.MissionStarted -= Forget;
            m_Director.MissionEnded -= OnMissionEnded;
            m_Director = null;
        }

        void OnMissionEnded(MissionResult result) => Forget();

        /// <summary>The run's items are destroyed with the run: just drop the references.</summary>
        void Forget()
        {
            if (m_Items == null) return;
            for (int i = 0; i < m_Items.Length; i++) m_Items[i] = null;
            Refresh();
        }

        void OnWristButton(PushButton button, HandRig hand) => Toggle();

        void Update()
        {
            Hook();
            // Destroyed items (end of a run) leave empty slots behind.
            bool changed = false;
            for (int i = 0; i < m_Items.Length; i++)
            {
                if (!ReferenceEquals(m_Items[i], null) && m_Items[i] == null) { m_Items[i] = null; changed = true; }
            }
            if (changed) Refresh();
            if (IsOpen && player != null && Vector3.Distance(panel.transform.position, player.HeadPosition) > autoCloseDistance) Close();
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public void Open()
        {
            if (panel == null || player == null || player.head == null) return;
            Vector3 forward = Vector3.ProjectOnPlane(player.head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            panel.transform.SetPositionAndRotation(player.head.position + forward * distance + Vector3.down * drop, Quaternion.LookRotation(forward, Vector3.up));
            panel.SetActive(true);
            SetStoredVisible(true);
            Refresh();
        }

        public void Close()
        {
            if (panel == null) return;
            SetStoredVisible(false);
            panel.SetActive(false);
        }

        void SetStoredVisible(bool visible)
        {
            foreach (var item in m_Items)
            {
                if (item != null && item.gameObject.activeSelf != visible) item.gameObject.SetActive(visible);
            }
        }

        /// <summary>Why this item cannot be stowed, or null if it can.</summary>
        public string Refusal(SalvageItem item)
        {
            if (item == null) return "Nothing to stow.";
            if (item.StoredIn != null) return "Already stowed.";
            if (item.IsSecured) return "Already in the locker.";
            if (item.IsMounted) return "Still bolted down.";
            if (item.definition != null && item.definition.bulky) return "Too big for a pocket.";
            if (Count >= Capacity) return "Pockets full.";
            return null;
        }

        /// <summary>Stows an item in a free slot (the given one if it is free, otherwise the first empty).</summary>
        public bool TryStore(SalvageItem item, int preferredSlot = -1)
        {
            if (Refusal(item) != null) return false;
            int slot = preferredSlot >= 0 && preferredSlot < Capacity && m_Items[preferredSlot] == null ? preferredSlot : -1;
            for (int i = 0; slot < 0 && i < Capacity; i++) if (m_Items[i] == null) slot = i;
            if (slot < 0) return false;

            m_Items[slot] = item;
            m_Scales[slot] = item.transform.localScale;
            item.StoredIn = this;
            if (item.Body != null)
            {
                if (!item.Body.isKinematic)
                {
                    item.Body.linearVelocity = Vector3.zero;
                    item.Body.angularVelocity = Vector3.zero;
                }
                item.Body.isKinematic = true;
            }

            // Shrink to pocket size, measured from its renderers.
            float longest = 0.05f;
            var bounds = new Bounds(item.transform.position, Vector3.zero);
            foreach (var r in item.GetComponentsInChildren<MeshRenderer>()) bounds.Encapsulate(r.bounds);
            longest = Mathf.Max(longest, Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)));
            float factor = Mathf.Min(1f, storedSize / longest);
            item.transform.SetParent(slots[slot], false);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            item.transform.localScale = m_Scales[slot] * factor;
            item.gameObject.SetActive(IsOpen);
            if (Sfx.LibraryOrNull != null) Sfx.PlayAt(Sfx.LibraryOrNull.toolHolster, slots[slot].position, 0.6f, 1.3f, 6f);
            Refresh();
            return true;
        }

        /// <summary>Called when a held item is let go. Stows it if the tray is open and it is over a free slot.</summary>
        public bool TryStoreReleased(SalvageItem item)
        {
            if (!IsOpen || Refusal(item) != null) return false;
            int best = -1;
            float bestDistance = storeRadius;
            for (int i = 0; i < Capacity; i++)
            {
                if (m_Items[i] != null || slots[i] == null) continue;
                float d = Vector3.Distance(slots[i].position, item.transform.position);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best >= 0 && TryStore(item, best);
        }

        /// <summary>Takes an item out (it is being grabbed, or the caller wants it back). It becomes an ordinary floating object again.</summary>
        public bool Release(SalvageItem item)
        {
            if (item == null) return false;
            for (int i = 0; i < Capacity; i++)
            {
                if (m_Items[i] != item) continue;
                m_Items[i] = null;
                item.StoredIn = null;
                var director = MissionDirector.Active;
                item.gameObject.SetActive(true);
                item.transform.SetParent(director != null ? director.salvageRoot : null, true);
                item.transform.localScale = m_Scales[i];
                if (item.Body != null) item.Body.isKinematic = false;
                Refresh();
                return true;
            }
            return false;
        }

        public SalvageItem Take(int slot)
        {
            var item = ItemAt(slot);
            return item != null && Release(item) ? item : null;
        }

        void Refresh()
        {
            if (header != null) header.text = "POCKETS  " + Count + " / " + Capacity + "     " + CarriedValue + " cr\nlet go of salvage on a slot to stow it";
        }
    }
}
