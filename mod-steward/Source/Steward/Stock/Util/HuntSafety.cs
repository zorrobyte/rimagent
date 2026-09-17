// Written for Autopilot (2026, MIT) as part of the synchronous Colony Manager Redux rewrite; modified for RimBridge (2026).
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Stock
{
    /// <summary>One definition of "dangerous game" shared by the hunting job, the planner, and the survey.</summary>
    public static class HuntSafety
    {
        public const float ManhunterChance = 0.1f;
        public const float BodySize = 2.0f;

        public static bool IsDangerous(PawnKindDef kind)
        {
            var race = kind?.RaceProps;
            if (race == null) return true;
            if (race.predator) return true;
            if (race.manhunterOnDamageChance >= ManhunterChance) return true;
            if (race.baseBodySize > BodySize) return true;
            if (Explodes(race)) return true;
            return false;
        }

        public static bool IsDangerous(Pawn animal) => animal?.kindDef == null || IsDangerous(animal.kindDef) || animal.BodySize > BodySize;

        /// <summary>Boomalopes, boomrats: any non-default death action.</summary>
        public static bool Explodes(RaceProperties race)
        {
            var da = race.deathAction;
            if (da == null) return false;
            var worker = da.workerClass;
            return worker != null && worker != typeof(DeathActionWorker_Simple);
        }

        public static string DangerReason(PawnKindDef kind)
        {
            var race = kind?.RaceProps;
            if (race == null) return "unknown";
            if (race.predator) return "predator";
            if (Explodes(race)) return "explodes";
            if (race.manhunterOnDamageChance >= ManhunterChance) return $"revenge {race.manhunterOnDamageChance * 100:F0}%";
            if (race.baseBodySize > BodySize) return "huge";
            return "";
        }

        /// <summary>May the colony hunt this kind right now?</summary>
        public static bool MayHunt(Map? map, PawnKindDef kind)
        {
            if (kind == null) return false;
            if (!IsDangerous(kind)) return true;
            if (StewardMod.Settings?.stock?.HuntPredators == true) return true;
            if (map?.mapPawns == null) return false;
            // A real hunting party can take on revenge-prone (not predator, not explosive) game.
            int ranged = map.mapPawns.FreeColonistsSpawned.Count(p => p.equipment?.Primary?.def.IsRangedWeapon == true);
            var race = kind.RaceProps;
            return ranged >= 3 && !race.predator && !Explodes(race) && race.baseBodySize <= 3.5f;
        }
    }
}
