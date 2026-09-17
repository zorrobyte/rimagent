// Fallback strings from Free Will Languages/English/Keyed/FreeWill.xml (MIT, Copyright (c) 2021 Paul Freeman; see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): RimBridge ships no Languages folder, so the vendored scorer resolves its keys here.
#nullable disable
using System.Collections.Generic;
using Verse;

namespace RimBridge.Steward.Scorer
{
    /// <summary>
    /// Translation shim for the vendored Free Will code. Because this class lives in RimBridge.Steward.Scorer, C#
    /// extension-method lookup finds these Translate/TranslateSimple overloads before Verse's (inner namespace wins),
    /// so the vendored files keep their original "Key".Translate() calls untouched. A real translation (vanilla keys
    /// like "Priority3") wins when one exists; otherwise the English table below; otherwise the key itself.
    /// </summary>
    public static class ScorerText
    {
        public static string TranslateSimple(this string key) => Resolve(key);

        public static TaggedString Translate(this string key) => new TaggedString(Resolve(key));

        public static TaggedString Translate(this string key, params NamedArgument[] args)
        {
            string s = Resolve(key);
            return args == null || args.Length == 0 ? new TaggedString(s) : s.Formatted(args);
        }

        public static string Resolve(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            try
            {
                if (Translator.TryTranslate(key, out TaggedString real)) return real.RawText;
            }
            catch { /* language data not loaded yet; fall through */ }
            return English.TryGetValue(key, out string s) ? s : key;
        }

        public static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            ["FreeWillWorkPreference"] = "Work preference",
            ["FreeWillWorkAssignment"] = "Work assignment",
            ["FreeWillPriorityDefault"] = "default",
            ["FreeWillPriorityFromGame"] = "work priority",
            ["FreeWillPriorityGlobalDefault"] = "base priority",
            ["FreeWillPriorityError"] = "error",
            ["FreeWillPriorityEnabled"] = "enabled",
            ["FreeWillPriorityDisabled"] = "disabled",
            ["FreeWillPriorityPermanentlyDisabled"] = "permanently disabled",
            ["FreeWillPriorityFirefightingDefault"] = "firefighting base value",
            ["FreeWillPriorityFirefightingAlways"] = "always fight fires",
            ["FreeWillPriorityPatientDefault"] = "patient base value",
            ["FreeWillPriorityPatientAlways"] = "always accept treatment",
            ["FreeWillPriorityBedrestDefault"] = "bedrest base value",
            ["FreeWillPriorityBedrestAlways"] = "always accept bedrest",
            ["FreeWillPriorityChildcareDefault"] = "childcare base value",
            ["FreeWillPriorityBasicWorkDefault"] = "basic work base value",
            ["FreeWillPriorityHaulingDefault"] = "hauling base value",
            ["FreeWillPriorityUrgentHaulingDefault"] = "urgent hauling base value",
            ["FreeWillPriorityCleaningDefault"] = "cleaning base value",
            ["FreeWillPriorityPawnDowned"] = "downed",
            ["FreeWillPriorityOtherPawnsDowned"] = "others downed",
            ["FreeWillPriorityNeedTreatment"] = "need treatment",
            ["FreeWillPriorityNeedTreatmentSelfTend"] = "can self tend",
            ["FreeWillPriorityOthersNeedTreatment"] = "others need treatment",
            ["FreeWillPriorityPetsInjured"] = "pets injured",
            ["FreeWillPriorityMechHaulers"] = "mech haulers",
            ["FreeWillPriorityPrisonersInjured"] = "prisoners injured",
            ["FreeWillPriorityFireInHomeArea"] = "home on fire",
            ["FreeWillPriorityFireOnMap"] = "fire in the area",
            ["FreeWillPriorityOperation"] = "operation scheduled",
            ["FreeWillPriorityHealth"] = "current health",
            ["FreeWillPriorityBuildingImmunity"] = "building immunity",
            ["FreeWillPriorityBored"] = "bored",
            ["FreeWillPriorityWasBored"] = "was recently bored",
            ["FreeWillPrioritySuppressionNeed"] = "slave suppression needed",
            ["FreeWillPriorityColonistLeftUnburied"] = "colonist left unburied",
            ["FreeWillPriorityRefueling"] = "refueling required",
            ["FreeWillPriorityFilthyCookingArea"] = "dirty cooking area",
            ["FreeWillPriorityFoodPoisoning"] = "food poisoning",
            ["FreeWillPriorityAnimalPenNeeded"] = "animal pen needed",
            ["FreeWillPriorityAnimalPenNotEnclosed"] = "animal pen not enclosed",
            ["FreeWillPriorityBestAtDoing"] = "best at doing",
            ["FreeWillPrioritySomeoneMuchMuchMuchBetterAtDoing"] = "{0} is amazing at this",
            ["FreeWillPrioritySomeoneMuchMuchBetterAtDoing"] = "{0} is great at this",
            ["FreeWillPrioritySomeoneMuchBetterAtDoing"] = "{0} is much better at this",
            ["FreeWillPrioritySomeoneBetterAtDoing"] = "{0} is better at this",
            ["FreeWillPrioritySomeoneMuchMuchMuchBetterIsDoing"] = "{0} is doing amazing at this",
            ["FreeWillPrioritySomeoneMuchMuchBetterIsDoing"] = "{0} is doing great at this",
            ["FreeWillPrioritySomeoneMuchBetterIsDoing"] = "{0} is doing much better at this",
            ["FreeWillPrioritySomeoneBetterIsDoing"] = "{0} is doing better at this",
            ["FreeWillPriorityNoOneElseDoing"] = "no one else doing",
            ["FreeWillPriorityMechanoidDamaged"] = "mechanoid damaged",
            ["FreeWillPriorityInspired"] = "inspired",
            ["FreeWillPriorityNotInHomeArea"] = "not in home area",
            ["FreeWillPriorityHungerLevel"] = "hunger level",
            ["FreeWillPriorityLowFood"] = "low food",
            ["FreeWillPriorityWeaponRange"] = "weapon range",
            ["FreeWillPriorityAteRawFood"] = "ate raw food",
            ["FreeWillPriorityOwnRoom"] = "should clean my room",
            ["FreeWillPriorityThingsDeteriorating"] = "things deteriorating",
            ["FreeWillPriorityBlight"] = "blight",
            ["FreeWillPriorityPruneGauranlenTree"] = "gauranlen tree needs pruning",
            ["FreeWillPriorityNeedWarmClothes"] = "need warm clothes",
            ["FreeWillPriorityAnimalsRoaming"] = "animals roaming",
            ["FreeWillPriorityBrawler"] = "brawler",
            ["FreeWillPriorityNoHuntingWeapon"] = "no hunting weapon",
            ["FreeWillPriorityCurrentlyDoing"] = "continuing current task",
            ["FreeWillPriorityMovementSpeed"] = "movement speed",
            ["FreeWillPriorityCarryingCapacity"] = "carrying capacity",
            ["FreeWillPriorityMechGestator"] = "mech in gestator ready",
            ["FreeWillPrioritySkillLevel"] = "skill level",
            ["FreeWillPriorityExpectionsExceeded"] = "not messy",
            ["FreeWillPriorityExpectionsMet"] = "hardly messy",
            ["FreeWillPriorityExpectionsUnmet"] = "a bit messy",
            ["FreeWillPriorityExpectionsLetDown"] = "very messy",
            ["FreeWillPriorityExpectionsIgnored"] = "mess everywhere",
            ["FreeWillPriorityBeautyDefault"] = "beauty default",
            ["FreeWillPriorityMinorAversionTo"] = "minor aversion to",
            ["FreeWillPriorityMajorAversionTo"] = "major aversion to",
            ["FreeWillPriorityMajorPassionFor"] = "major passion for {0}",
            ["FreeWillPriorityMinorPassionFor"] = "minor passion for {0}",
            ["FreeWillPriorityApathy"] = "apathy for {0}",
            ["FreeWillPriorityNatural"] = "natural at {0}",
            ["FreeWillPriorityCritical"] = "{0} is critical",
            ["FreeWillPriorityCompulsiveItch"] = "itching to do",
            ["FreeWillPriorityCompulsiveNeed"] = "needing to do",
            ["FreeWillPriorityCompulsiveObsession"] = "obsessed with",
            ["FreeWillPriorityCompulsiveDemand"] = "demand to do",
            ["FreeWillPriorityCompulsiveWithdrawl"] = "in withdrawal from",
            ["FreeWillPriorityCompulsiveYearning"] = "yearning to do",
            ["FreeWillPriorityCompulsiveTantrum"] = "tantrum over",
            ["FreeWillPriorityCompulsiveHysteria"] = "hysterical about",
            ["FreeWillPriorityInvigorating"] = "invigorated by",
            ["FreeWillPriorityBoredBy"] = "bored by",
            ["FreeWillPriorityReactionInitial"] = "allergic symptoms from",
            ["FreeWillPriorityReactionItching"] = "itching from",
            ["FreeWillPriorityReactionSneezing"] = "sneezing from",
            ["FreeWillPriorityReactionSwelling"] = "swelling from",
            ["FreeWillPriorityReactionAnaphylaxis"] = "can't breath from",
            ["FreeWillPriorityNoReaction"] = "no allergic symptoms from",
            ["FreeWillPriorityNoFreeWill"] = "no free will",
            ["FreeWillPriorityColonyPolicy"] = "colony policy",
        };
    }
}
