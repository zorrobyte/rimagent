// ScorerSettings derived from Free Will ScorerSettings.cs (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): settings UI removed (RimBridge exposes these through steward.settings), stored on StewardModSettings.steward.
using System.Collections.Generic;
using Verse;

namespace RimBridge.Steward
{
    /// <summary>Weights for the work-priority scorer. Field names are the keys accepted by steward.settings / posture weights.</summary>
    public class ScorerSettings : IExposable
    {
        public const bool ConsiderBrawlersNotHuntingDefault = true;
        public const bool ConsiderHasHuntingWeaponDefault = true;
        public const float ConsiderMovementSpeedDefault = 1.0f;
        public const float ConsiderPassionsDefault = 1.0f;
        public const float ConsiderBeautyDefault = 1.0f;
        public const float ConsiderBestAtDoingDefault = 0.0f;
        public const float ConsiderFoodPoisoningDefault = 1.0f;
        public const float ConsiderLowFoodDefault = 1.0f;
        public const float ConsiderWeaponRangeDefault = 1.0f;
        public const float ConsiderOwnRoomDefault = 1.0f;
        public const float ConsiderPlantsBlightedDefault = 1.0f;
        public const float ConsiderGauranlenPruningDefault = 1.0f;

        /// <summary>Default switch for the scorer (mod settings). Runtime override: StewardSwitch.ScorerEnabled.</summary>
        public bool Enabled = true;
        /// <summary>1 = one (pawn, work type) evaluation per tick (Free Will default). Raise to spread load.</summary>
        public int TicksBetweenActions = 1;

        public bool ConsiderBrawlersNotHunting = ConsiderBrawlersNotHuntingDefault;
        public bool ConsiderHasHuntingWeapon = ConsiderHasHuntingWeaponDefault;
        public float ConsiderMovementSpeed = ConsiderMovementSpeedDefault;
        public float ConsiderPassions = ConsiderPassionsDefault;
        public float ConsiderBeauty = ConsiderBeautyDefault;
        public float ConsiderBestAtDoing = ConsiderBestAtDoingDefault;
        public float ConsiderFoodPoisoning = ConsiderFoodPoisoningDefault;
        public float ConsiderLowFood = ConsiderLowFoodDefault;
        public float ConsiderWeaponRange = ConsiderWeaponRangeDefault;
        public float ConsiderOwnRoom = ConsiderOwnRoomDefault;
        public float ConsiderPlantsBlighted = ConsiderPlantsBlightedDefault;
        public float ConsiderGauranlenPruning = ConsiderGauranlenPruningDefault;

        /// <summary>Player baseline per WorkTypeDef defName, -1..1 (Free Will's global sliders).</summary>
        public Dictionary<string, float> globalWorkAdjustments = new Dictionary<string, float>();

        /// <summary>Names of the float weights that posture multipliers and steward.settings may address.</summary>
        public static readonly string[] WeightNames =
        {
            "ConsiderMovementSpeed", "ConsiderPassions", "ConsiderBeauty", "ConsiderBestAtDoing", "ConsiderFoodPoisoning",
            "ConsiderLowFood", "ConsiderWeaponRange", "ConsiderOwnRoom", "ConsiderPlantsBlighted", "ConsiderGauranlenPruning",
        };

        public float GetWeight(string name) => name switch
        {
            "ConsiderMovementSpeed" => ConsiderMovementSpeed,
            "ConsiderPassions" => ConsiderPassions,
            "ConsiderBeauty" => ConsiderBeauty,
            "ConsiderBestAtDoing" => ConsiderBestAtDoing,
            "ConsiderFoodPoisoning" => ConsiderFoodPoisoning,
            "ConsiderLowFood" => ConsiderLowFood,
            "ConsiderWeaponRange" => ConsiderWeaponRange,
            "ConsiderOwnRoom" => ConsiderOwnRoom,
            "ConsiderPlantsBlighted" => ConsiderPlantsBlighted,
            "ConsiderGauranlenPruning" => ConsiderGauranlenPruning,
            _ => 0f,
        };

        /// <summary>Returns false for an unknown name.</summary>
        public bool SetWeight(string name, float value)
        {
            switch (name)
            {
                case "ConsiderMovementSpeed": ConsiderMovementSpeed = value; return true;
                case "ConsiderPassions": ConsiderPassions = value; return true;
                case "ConsiderBeauty": ConsiderBeauty = value; return true;
                case "ConsiderBestAtDoing": ConsiderBestAtDoing = value; return true;
                case "ConsiderFoodPoisoning": ConsiderFoodPoisoning = value; return true;
                case "ConsiderLowFood": ConsiderLowFood = value; return true;
                case "ConsiderWeaponRange": ConsiderWeaponRange = value; return true;
                case "ConsiderOwnRoom": ConsiderOwnRoom = value; return true;
                case "ConsiderPlantsBlighted": ConsiderPlantsBlighted = value; return true;
                case "ConsiderGauranlenPruning": ConsiderGauranlenPruning = value; return true;
                default: return false;
            }
        }

        public ScorerSettings Clone()
        {
            return new ScorerSettings
            {
                Enabled = Enabled,
                TicksBetweenActions = TicksBetweenActions,
                ConsiderBrawlersNotHunting = ConsiderBrawlersNotHunting,
                ConsiderHasHuntingWeapon = ConsiderHasHuntingWeapon,
                ConsiderMovementSpeed = ConsiderMovementSpeed,
                ConsiderPassions = ConsiderPassions,
                ConsiderBeauty = ConsiderBeauty,
                ConsiderBestAtDoing = ConsiderBestAtDoing,
                ConsiderFoodPoisoning = ConsiderFoodPoisoning,
                ConsiderLowFood = ConsiderLowFood,
                ConsiderWeaponRange = ConsiderWeaponRange,
                ConsiderOwnRoom = ConsiderOwnRoom,
                ConsiderPlantsBlighted = ConsiderPlantsBlighted,
                ConsiderGauranlenPruning = ConsiderGauranlenPruning,
                globalWorkAdjustments = globalWorkAdjustments, // shared by reference, never cloned
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "enabled", true);
            Scribe_Values.Look(ref TicksBetweenActions, "ticksBetweenActions", 1);
            Scribe_Values.Look(ref ConsiderMovementSpeed, "considerMovementSpeed", ConsiderMovementSpeedDefault, true);
            Scribe_Values.Look(ref ConsiderPassions, "considerPassions", ConsiderPassionsDefault, true);
            Scribe_Values.Look(ref ConsiderBeauty, "considerBeauty", ConsiderBeautyDefault, true);
            Scribe_Values.Look(ref ConsiderBestAtDoing, "considerBestAtDoing", ConsiderBestAtDoingDefault, true);
            Scribe_Values.Look(ref ConsiderFoodPoisoning, "considerFoodPoisoning", ConsiderFoodPoisoningDefault, true);
            Scribe_Values.Look(ref ConsiderLowFood, "considerLowFood", ConsiderLowFoodDefault, true);
            Scribe_Values.Look(ref ConsiderWeaponRange, "considerWeaponRange", ConsiderWeaponRangeDefault, true);
            Scribe_Values.Look(ref ConsiderOwnRoom, "considerOwnRoom", ConsiderOwnRoomDefault, true);
            Scribe_Values.Look(ref ConsiderBrawlersNotHunting, "considerBrawlersNotHunting", ConsiderBrawlersNotHuntingDefault, true);
            Scribe_Values.Look(ref ConsiderHasHuntingWeapon, "considerHasHuntingWeapon", ConsiderHasHuntingWeaponDefault, true);
            Scribe_Values.Look(ref ConsiderPlantsBlighted, "considerPlantsBlighted", ConsiderPlantsBlightedDefault, true);
            Scribe_Values.Look(ref ConsiderGauranlenPruning, "considerGauranlenPruning", ConsiderGauranlenPruningDefault, true);
            Scribe_Collections.Look(ref globalWorkAdjustments, "workTypeAdjustments", LookMode.Value, LookMode.Value);
            globalWorkAdjustments ??= new Dictionary<string, float>();
        }
    }

    /// <summary>Settings for the stock layer (synchronous Colony Manager rewrite).</summary>
    public class StockSettings : IExposable
    {
        /// <summary>Default switch for stock jobs (mod settings). Runtime override: StewardSwitch.StockEnabled.</summary>
        public bool Enabled = true;
        public bool EnableForestry = true;
        public bool EnableForaging = true;
        public bool EnableHunting = true;
        public bool EnableMining = true;
        public bool EnableProduction = true;
        public bool EnableLivestock = true;
        /// <summary>Run the most overdue job every N ticks (one job per scheduler tick).</summary>
        public int SchedulerIntervalTicks = 250;
        public int MaxDesignationsPerJob = 40;
        /// <summary>Rescale auto-scaled targets when the colonist count changes.</summary>
        public bool ScaleTargetsWithColonists = true;
        public int BudgetMsWarn = 20;
        public bool HuntPredators = false;
        public bool MineThickRoofs = true;
        /// <summary>Distance cap from home for stock-job targets, in cells. 0 = no cap: the whole map is fair game.
        /// Default 0 on purpose — a colony that refuses to hunt, log or mine something because it is "too far"
        /// stalls its own stock jobs while the resource sits there in plain sight. Distance is a pathing cost,
        /// not a rule. Set it non-zero only to deliberately keep pawns close to home.</summary>
        public int MaxWorkRadius = 0;
        /// <summary>Never target things this close to hostiles, hives, or other known dangers.</summary>
        public int DangerAvoidRadius = 30;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "enabled", true);
            Scribe_Values.Look(ref EnableForestry, "enableForestry", true);
            Scribe_Values.Look(ref EnableForaging, "enableForaging", true);
            Scribe_Values.Look(ref EnableHunting, "enableHunting", true);
            Scribe_Values.Look(ref EnableMining, "enableMining", true);
            Scribe_Values.Look(ref EnableProduction, "enableProduction", true);
            Scribe_Values.Look(ref EnableLivestock, "enableLivestock", true);
            Scribe_Values.Look(ref SchedulerIntervalTicks, "schedulerIntervalTicks", 250);
            Scribe_Values.Look(ref MaxDesignationsPerJob, "maxDesignationsPerJob", 40);
            Scribe_Values.Look(ref ScaleTargetsWithColonists, "scaleTargetsWithColonists", true);
            Scribe_Values.Look(ref BudgetMsWarn, "budgetMsWarn", 20);
            Scribe_Values.Look(ref HuntPredators, "huntPredators", false);
            Scribe_Values.Look(ref MineThickRoofs, "mineThickRoofs", true);
            Scribe_Values.Look(ref MaxWorkRadius, "maxWorkRadius", 0);
            Scribe_Values.Look(ref DangerAvoidRadius, "dangerAvoidRadius", 30);
        }
    }

    /// <summary>Container stored on BridgeSettings as "steward" (StewardMod.Settings).</summary>
    public class StewardSettings : IExposable
    {
        public ScorerSettings scorer = new ScorerSettings();
        public StockSettings stock = new StockSettings();

        public void ExposeData()
        {
            Scribe_Deep.Look(ref scorer, "scorer");
            Scribe_Deep.Look(ref stock, "stock");
            scorer ??= new ScorerSettings();
            stock ??= new StockSettings();
        }
    }

    /// <summary>
    /// Runtime on/off for the two subsystems (steward.enable). Persisted per game in StewardGame; falls back to the
    /// mod-settings default when no override was set. Both default ON so the steward works without the agent.
    /// </summary>
    public static class StewardSwitch
    {
        public static bool ScorerEnabled
        {
            get => StewardGame.Current?.scorerOverride ?? StewardMod.Settings?.scorer?.Enabled ?? true;
            set { var g = StewardGame.Current; if (g != null) g.scorerOverride = value; else if (StewardMod.Settings?.scorer != null) StewardMod.Settings.scorer.Enabled = value; }
        }

        public static bool StockEnabled
        {
            get => StewardGame.Current?.stockOverride ?? StewardMod.Settings?.stock?.Enabled ?? true;
            set { var g = StewardGame.Current; if (g != null) g.stockOverride = value; else if (StewardMod.Settings?.stock != null) StewardMod.Settings.stock.Enabled = value; }
        }
    }
}
