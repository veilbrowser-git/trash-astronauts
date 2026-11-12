using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// The HELMET LAMP on the company store counter: its buy button, labelled like the store's other buttons (price,
    /// or OWNED, CLOSED, or what you still need), bought once with banked credits and kept in the save.
    /// Sits beside <see cref="ShopPanel"/> on the store and works the same way.
    /// </summary>
    public sealed class HeadLampShopButton : MonoBehaviour
    {
        public MissionDirector director;
        public PushButton button;
        [Tooltip("The lens of the lamp on display: lit once it is yours.")]
        public Renderer displayLens;
        public float refreshInterval = 0.5f;

        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        MaterialPropertyBlock m_Block;
        float m_NextRefresh;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (button != null) button.Pressed += OnPressed;
            if (director != null) director.ProfileChanged += Refresh;
        }

        void OnDisable()
        {
            if (button != null) button.Pressed -= OnPressed;
            if (director != null) director.ProfileChanged -= Refresh;
        }

        void Start() => Refresh();

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + refreshInterval;
            Refresh();
        }

        void OnPressed(PushButton b, HandRig hand)
        {
            if (director == null) return;
            if (director.PurchaseShopItem(ShopItem.HeadLamp) && Sfx.Library != null)
                Sfx.PlayAt(Sfx.Library.deposit, transform.position, 0.7f, 1.2f, 6f);
            Refresh();
        }

        public void Refresh()
        {
            if (director == null || director.Profile == null || button == null) return;
            var config = director.Config;
            bool owned = director.Profile.IsOwned(ShopItem.HeadLamp);
            bool open = director.Phase != MissionPhase.Active;
            int cost = config.GetShopCost(ShopItem.HeadLamp);
            const string title = "HELMET LAMP";
            if (owned)
            {
                button.SetLabel(title + "\nOWNED: tap helmet");
                button.SetInteractable(false);
            }
            else if (!open)
            {
                button.SetLabel(title + "\nCLOSED");
                button.SetInteractable(false);
            }
            else if (director.Profile.CanPurchase(ShopItem.HeadLamp, config))
            {
                button.SetLabel(title + "\nhands free - " + cost + " cr");
                button.SetInteractable(true);
            }
            else
            {
                button.SetLabel(title + "\nNeed " + cost + " cr");
                button.SetInteractable(false);
            }

            if (displayLens != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                displayLens.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_Emission, owned ? new Color(2.4f, 2.2f, 1.7f) : new Color(0.35f, 0.33f, 0.28f));
                displayLens.SetPropertyBlock(m_Block);
            }
        }
    }
}
