using System;
using System.Collections.Generic;
using System.Text;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// A fingerprint of everything in a <see cref="MissionSession"/> that must be the same on every
    /// machine: phase, run identity, quota, the salvage ledger, open panels, totals and the outcome.
    /// Tests compare the host's digest with each client mirror's.
    ///
    /// The clock (elapsed time, oxygen remaining) is streamed and extrapolated rather than replicated
    /// exactly, so it is only included when asked for. Banked is per device and never included.
    /// </summary>
    public static class SessionDigest
    {
        public static uint Compute(MissionSession session, bool includeClock = false)
        {
            if (session == null) return 0u;
            uint h = NetIdTable.FnvOffset;
            h = NetIdTable.Fnv(h, (byte)session.Phase);
            h = NetIdTable.Fnv(h, session.RunIndex);
            h = NetIdTable.Fnv(h, session.Seed);
            h = NetIdTable.Fnv(h, session.Quota);
            h = NetIdTable.Fnv(h, session.BonusMultiplier);
            h = NetIdTable.Fnv(h, session.Oxygen.Capacity);
            h = NetIdTable.Fnv(h, session.DepositedValue);
            h = NetIdTable.Fnv(h, session.DepositedCount);
            h = NetIdTable.Fnv(h, session.TrickBonus);
            h = NetIdTable.Fnv(h, session.TrickShots);

            var items = SortedItems(session);
            h = NetIdTable.Fnv(h, items.Count);
            foreach (var item in items)
            {
                h = NetIdTable.Fnv(h, item.Id);
                h = NetIdTable.Fnv(h, item.DefinitionId);
                h = NetIdTable.Fnv(h, item.Value);
                h = NetIdTable.Fnv(h, (byte)item.State);
                h = NetIdTable.Fnv(h, item.CollectedBy.Value);
                h = NetIdTable.Fnv(h, item.DepositedBy.Value);
                h = NetIdTable.Fnv(h, item.TrickShot);
            }

            var panels = SortedPanels(session);
            h = NetIdTable.Fnv(h, panels.Count);
            foreach (var panel in panels) h = NetIdTable.Fnv(h, panel);

            var result = session.Result;
            h = NetIdTable.Fnv(h, result != null);
            if (result != null)
            {
                h = NetIdTable.Fnv(h, (byte)result.Outcome);
                h = NetIdTable.Fnv(h, result.Quota);
                h = NetIdTable.Fnv(h, result.DepositedValue);
                h = NetIdTable.Fnv(h, result.DepositedCount);
                h = NetIdTable.Fnv(h, result.TrickBonus);
                h = NetIdTable.Fnv(h, result.TrickShots);
                h = NetIdTable.Fnv(h, result.Bonus);
                h = NetIdTable.Fnv(h, result.Payout);
                if (includeClock)
                {
                    h = NetIdTable.Fnv(h, result.OxygenRemainingSeconds);
                    h = NetIdTable.Fnv(h, result.ElapsedSeconds);
                }
            }

            if (includeClock)
            {
                h = NetIdTable.Fnv(h, session.ElapsedSeconds);
                h = NetIdTable.Fnv(h, session.Oxygen.Remaining);
            }
            return h;
        }

        /// <summary>The same content as <see cref="Compute"/>, readable, for a failing test's message.</summary>
        public static string Describe(MissionSession session, bool includeClock = false)
        {
            if (session == null) return "(no session)";
            var sb = new StringBuilder();
            sb.Append(session.Phase).Append(" run ").Append(session.RunIndex).Append(" seed ").Append(session.Seed)
                .Append(" quota ").Append(session.Quota).Append(" bonusx").Append(session.BonusMultiplier.ToString("R"))
                .Append(" tank ").Append(session.Oxygen.Capacity.ToString("R"))
                .Append(" deposited ").Append(session.DepositedValue).Append('/').Append(session.DepositedCount)
                .Append(" tricks ").Append(session.TrickBonus).Append('/').Append(session.TrickShots);
            if (includeClock)
                sb.Append(" t ").Append(session.ElapsedSeconds.ToString("R")).Append(" o2 ").Append(session.Oxygen.Remaining.ToString("R"));
            foreach (var item in SortedItems(session))
            {
                sb.Append("\n  ").Append(item.Id).Append(' ').Append(item.DefinitionId).Append(' ').Append(item.Value).Append(' ').Append(item.State)
                    .Append(" c").Append(item.CollectedBy.Value).Append(" d").Append(item.DepositedBy.Value);
                if (item.TrickShot) sb.Append(" trick");
            }
            foreach (var panel in SortedPanels(session)) sb.Append("\n  open ").Append(panel);
            var result = session.Result;
            if (result != null)
            {
                sb.Append("\n  result ").Append(result.Outcome).Append(" quota ").Append(result.Quota)
                    .Append(" deposited ").Append(result.DepositedValue).Append('/').Append(result.DepositedCount)
                    .Append(" tricks ").Append(result.TrickBonus).Append('/').Append(result.TrickShots)
                    .Append(" bonus ").Append(result.Bonus).Append(" payout ").Append(result.Payout);
                if (includeClock)
                    sb.Append(" o2 ").Append(result.OxygenRemainingSeconds.ToString("R")).Append(" t ").Append(result.ElapsedSeconds.ToString("R"));
            }
            return sb.ToString();
        }

        static List<SalvageRecord> SortedItems(MissionSession session)
        {
            var items = new List<SalvageRecord>(session.Items);
            items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return items;
        }

        static List<string> SortedPanels(MissionSession session)
        {
            var panels = new List<string>(session.OpenPanels);
            panels.Sort(StringComparer.Ordinal);
            return panels;
        }
    }
}
