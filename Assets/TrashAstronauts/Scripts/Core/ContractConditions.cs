using System;

namespace TrashAstronauts.Core
{
    /// <summary>Something different about this particular contract. Chosen from the run's seed, shown on the mission board.</summary>
    public enum ContractCondition
    {
        Standard = 0,
        /// <summary>Everything aboard is worth more.</summary>
        RichWreck = 1,
        /// <summary>The tank was not filled properly. Less oxygen; hazard pay.</summary>
        ThinAir = 2,
        /// <summary>The wreck's power keeps failing. Many more blackouts; hazard pay.</summary>
        PowerFault = 3,
        /// <summary>Webs across every route, and more of the small ones. Hazard pay.</summary>
        Infested = 4,
        /// <summary>It is already awake when you arrive. Hazard pay.</summary>
        LightSleeper = 5,
        /// <summary>It sleeps for a long time before it stirs.</summary>
        DeadCalm = 6,
    }

    public static class ContractConditions
    {
        public static ContractCondition Pick(int seed)
        {
            var rng = new Random(seed ^ 0x3c6ef372);
            if (rng.NextDouble() < 0.3) return ContractCondition.Standard;
            return (ContractCondition)(1 + rng.Next(6));
        }

        public static string Title(ContractCondition c)
        {
            switch (c)
            {
                case ContractCondition.RichWreck: return "RICH WRECK";
                case ContractCondition.ThinAir: return "THIN AIR";
                case ContractCondition.PowerFault: return "POWER FAULT";
                case ContractCondition.Infested: return "INFESTED";
                case ContractCondition.LightSleeper: return "LIGHT SLEEPER";
                case ContractCondition.DeadCalm: return "DEAD CALM";
                default: return "STANDARD";
            }
        }

        public static string Describe(ContractCondition c)
        {
            switch (c)
            {
                case ContractCondition.RichWreck: return "Everything aboard is worth 30% more.";
                case ContractCondition.ThinAir: return "A fifth less oxygen. Bonus +50%.";
                case ContractCondition.PowerFault: return "The lights keep failing. Bonus +50%.";
                case ContractCondition.Infested: return "Webs across every route. Bonus +50%.";
                case ContractCondition.LightSleeper: return "It is awake already. Bonus +50%.";
                case ContractCondition.DeadCalm: return "It will sleep for over a minute.";
                default: return "Nothing unusual reported.";
            }
        }

        public static float ValueMultiplier(ContractCondition c) => c == ContractCondition.RichWreck ? 1.3f : 1f;
        public static float OxygenMultiplier(ContractCondition c) => c == ContractCondition.ThinAir ? 0.8f : 1f;
        public static float BlackoutDelayMultiplier(ContractCondition c) => c == ContractCondition.PowerFault ? 0.35f : 1f;
        public static float WakeDelayMultiplier(ContractCondition c) => c == ContractCondition.LightSleeper ? 0.15f : c == ContractCondition.DeadCalm ? 3.75f : 1f;
        /// <summary>Chance (0..1) that each web tripwire is strung this contract.</summary>
        public static float WebChance(ContractCondition c) => c == ContractCondition.Infested ? 1f : 0.55f;
        public static float BonusMultiplier(ContractCondition c)
        {
            switch (c)
            {
                case ContractCondition.ThinAir:
                case ContractCondition.PowerFault:
                case ContractCondition.Infested:
                case ContractCondition.LightSleeper: return 1.5f;
                default: return 1f;
            }
        }
    }
}
