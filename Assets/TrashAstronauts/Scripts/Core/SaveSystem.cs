using System;
using System.IO;
using UnityEngine;

namespace TrashAstronauts.Core
{
    public enum Handedness
    {
        Right = 0,
        Left = 1,
    }

    /// <summary>Everything that persists between sessions. Mid-mission state is deliberately not saved.</summary>
    [Serializable]
    public sealed class SaveData
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public int totalCredits;
        public int oxygenUpgradeLevel;
        public int handedness;
        public float snapTurnDegrees = 45f;
        public float seatedHeightOffset;
        /// <summary>Comfort option: keep drifting after letting go in zero-g (true) or stop at once (false).</summary>
        public bool zeroGDrift = true;
        /// <summary>The creature can hear the player's voice through the headset microphone.</summary>
        public bool voiceDetection = true;
        /// <summary>
        /// Co-op proximity voice chat: while talking, this player's voice is sent to the crew, and the crew's voices
        /// are played from their helmets. Needs <see cref="voiceDetection"/> (MIC) on to send; off stops both ways.
        /// </summary>
        public bool voiceChat = true;
        public bool tutorialCompleted;
        public int tranqDarts;
        public bool scannerUpgrade;
        public bool cutterUpgrade;
        public int runsStarted;
        public int contractsCompleted;
        /// <summary>The biggest payout from a single contract.</summary>
        public int bestHaul;

        public Handedness Handedness
        {
            get => handedness == 1 ? Core.Handedness.Left : Core.Handedness.Right;
            set => handedness = value == Core.Handedness.Left ? 1 : 0;
        }

        public const float MinHeightOffset = -0.3f;
        public const float MaxHeightOffset = 0.9f;
        public static readonly float[] SnapTurnOptions = { 30f, 45f, 60f, 90f };

        /// <summary>Clamps every field into a legal range so a hand-edited or damaged file can never break the game.</summary>
        public void Sanitize(int maxOxygenUpgradeLevel = 1)
        {
            version = CurrentVersion;
            totalCredits = Mathf.Clamp(totalCredits, 0, 999999999);
            oxygenUpgradeLevel = Mathf.Clamp(oxygenUpgradeLevel, 0, Mathf.Max(0, maxOxygenUpgradeLevel));
            handedness = handedness == 1 ? 1 : 0;
            if (float.IsNaN(snapTurnDegrees) || float.IsInfinity(snapTurnDegrees)) snapTurnDegrees = 45f;
            snapTurnDegrees = NearestSnapOption(snapTurnDegrees);
            if (float.IsNaN(seatedHeightOffset) || float.IsInfinity(seatedHeightOffset)) seatedHeightOffset = 0f;
            seatedHeightOffset = Mathf.Clamp(seatedHeightOffset, MinHeightOffset, MaxHeightOffset);
            tranqDarts = Mathf.Clamp(tranqDarts, 0, 99);
            runsStarted = Mathf.Max(0, runsStarted);
            contractsCompleted = Mathf.Max(0, contractsCompleted);
        }

        static float NearestSnapOption(float value)
        {
            float best = SnapTurnOptions[0];
            foreach (float option in SnapTurnOptions)
            {
                if (Mathf.Abs(option - value) < Mathf.Abs(best - value)) best = option;
            }
            return best;
        }
    }

    public interface ISaveStorage
    {
        bool TryRead(out string json);
        void Write(string json);
        void Delete();
    }

    /// <summary>In-memory storage for tests.</summary>
    public sealed class MemorySaveStorage : ISaveStorage
    {
        public string Contents;
        public int WriteCount;

        public bool TryRead(out string json)
        {
            json = Contents;
            return Contents != null;
        }

        public void Write(string json)
        {
            Contents = json;
            WriteCount++;
        }

        public void Delete() => Contents = null;
    }

    /// <summary>
    /// JSON file in Application.persistentDataPath (app-private internal storage on Quest, no permissions needed).
    /// Writes go to a temp file first and the previous save is kept as a backup, so a crash or power loss
    /// mid-write cannot destroy progress.
    /// </summary>
    public sealed class FileSaveStorage : ISaveStorage
    {
        readonly string m_Path;
        string TempPath => m_Path + ".tmp";
        string BackupPath => m_Path + ".bak";

        public FileSaveStorage(string path) { m_Path = path; }

        public static FileSaveStorage Default()
        {
            return new FileSaveStorage(Path.Combine(Application.persistentDataPath, "trash_astronauts_save.json"));
        }

        public bool TryRead(out string json)
        {
            json = null;
            try
            {
                if (File.Exists(m_Path))
                {
                    json = File.ReadAllText(m_Path);
                    if (SaveSerializer.LooksValid(json)) return true;
                }
                if (File.Exists(BackupPath))
                {
                    json = File.ReadAllText(BackupPath);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] Could not read save file: " + e.Message);
            }
            return json != null;
        }

        public void Write(string json)
        {
            try
            {
                string directory = Path.GetDirectoryName(m_Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(TempPath, json);
                if (File.Exists(m_Path)) File.Copy(m_Path, BackupPath, true);
                File.Copy(TempPath, m_Path, true);
                File.Delete(TempPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] Could not write save file: " + e.Message);
            }
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(m_Path)) File.Delete(m_Path);
                if (File.Exists(BackupPath)) File.Delete(BackupPath);
                if (File.Exists(TempPath)) File.Delete(TempPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] Could not delete save file: " + e.Message);
            }
        }
    }

    public static class SaveSerializer
    {
        public static string ToJson(SaveData data) => JsonUtility.ToJson(data, true);

        public static bool LooksValid(string json) => TryParse(json, out _);

        public static bool TryParse(string json, out SaveData data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            string trimmed = json.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}') return false;
            try
            {
                data = JsonUtility.FromJson<SaveData>(trimmed);
            }
            catch (Exception)
            {
                data = null;
            }
            return data != null;
        }
    }

    /// <summary>
    /// Owns the persistent profile: credits, the oxygen upgrade and comfort settings.
    /// Missing, empty or corrupt saves fall back to defaults instead of throwing.
    /// </summary>
    public sealed class ProfileService
    {
        readonly ISaveStorage m_Storage;
        readonly int m_MaxOxygenUpgradeLevel;

        public SaveData Data { get; private set; } = new SaveData();
        /// <summary>True if the last Load found no usable save and started from defaults.</summary>
        public bool LoadedFromDefaults { get; private set; }

        public event Action Changed;

        public ProfileService(ISaveStorage storage, int maxOxygenUpgradeLevel = 1)
        {
            m_Storage = storage;
            m_MaxOxygenUpgradeLevel = Mathf.Max(0, maxOxygenUpgradeLevel);
        }

        public void Load()
        {
            SaveData loaded = null;
            bool found = false;
            try
            {
                found = m_Storage != null && m_Storage.TryRead(out string json) && SaveSerializer.TryParse(json, out loaded);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Save] Load failed, using defaults: " + e.Message);
            }

            LoadedFromDefaults = !found || loaded == null;
            Data = LoadedFromDefaults ? new SaveData() : loaded;
            Data.Sanitize(m_MaxOxygenUpgradeLevel);
            Changed?.Invoke();
        }

        public void Save()
        {
            Data.Sanitize(m_MaxOxygenUpgradeLevel);
            m_Storage?.Write(SaveSerializer.ToJson(Data));
        }

        public void NotifyChangedAndSave()
        {
            Save();
            Changed?.Invoke();
        }

        public void RecordRunStarted()
        {
            Data.runsStarted++;
            NotifyChangedAndSave();
        }

        /// <summary>Called once per finished run by <see cref="MissionSession.BankInto"/>.</summary>
        /// <summary>True if the run just recorded beat the best haul so far.</summary>
        public bool LastRunWasRecord { get; private set; }

        public void RecordRunFinished(int payout, bool extracted)
        {
            if (payout > 0) Data.totalCredits += payout;
            if (extracted) Data.contractsCompleted++;
            LastRunWasRecord = extracted && payout > Data.bestHaul;
            if (LastRunWasRecord) Data.bestHaul = payout;
            NotifyChangedAndSave();
        }

        public bool CanPurchaseOxygenUpgrade(MissionConfig config)
        {
            return config != null
                && Data.oxygenUpgradeLevel < Mathf.Min(config.maxOxygenUpgradeLevel, m_MaxOxygenUpgradeLevel)
                && Data.totalCredits >= config.oxygenUpgradeCost;
        }

        public bool TryPurchaseOxygenUpgrade(MissionConfig config)
        {
            if (!CanPurchaseOxygenUpgrade(config)) return false;
            Data.totalCredits -= config.oxygenUpgradeCost;
            Data.oxygenUpgradeLevel++;
            NotifyChangedAndSave();
            return true;
        }

        public bool IsOwned(ShopItem item)
        {
            switch (item)
            {
                case ShopItem.ScannerRange: return Data.scannerUpgrade;
                case ShopItem.CutterCoolant: return Data.cutterUpgrade;
                default: return false; // consumables are never "owned"
            }
        }

        public bool CanPurchase(ShopItem item, MissionConfig config)
        {
            if (config == null || IsOwned(item)) return false;
            if (item == ShopItem.TranqDart && Data.tranqDarts >= config.maxTranqDarts) return false;
            return Data.totalCredits >= config.GetShopCost(item);
        }

        /// <summary>Buys a shop item with banked credits. Upgrades can be bought once; darts up to the carry limit.</summary>
        public bool TryPurchase(ShopItem item, MissionConfig config)
        {
            if (!CanPurchase(item, config)) return false;
            Data.totalCredits -= config.GetShopCost(item);
            switch (item)
            {
                case ShopItem.TranqDart: Data.tranqDarts++; break;
                case ShopItem.ScannerRange: Data.scannerUpgrade = true; break;
                case ShopItem.CutterCoolant: Data.cutterUpgrade = true; break;
            }
            NotifyChangedAndSave();
            return true;
        }

        /// <summary>Uses up one tranquilizer dart. Returns false if there are none.</summary>
        public bool TryConsumeTranqDart()
        {
            if (Data.tranqDarts <= 0) return false;
            Data.tranqDarts--;
            NotifyChangedAndSave();
            return true;
        }

        public void ResetAll()
        {
            Data = new SaveData();
            m_Storage?.Delete();
            Changed?.Invoke();
        }
    }
}
