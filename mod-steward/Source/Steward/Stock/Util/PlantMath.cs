// Derived from Colony Manager Redux Helpers/Utilities/Utilities_Plants.cs (MIT, see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
// Pure logic: no Verse dependency (compiled into the test project).
using System;
using System.Collections.Generic;

namespace RimBridge.Steward.Stock
{
    public static class PlantMath
    {
        /// <summary>How many of the (yield-sorted, lowest value first) existing designations to remove so the count stays just above target.</summary>
        public static int ComputeReduceCount(int startingCount, IReadOnlyList<int> sortedYields, int startingDesignationCount,
            Func<int, bool> countMeetsTarget, Func<int, bool> shouldRemoveMoreDesignations)
        {
            int count = startingCount, designationCount = startingDesignationCount, removeCount = 0;
            for (; removeCount < sortedYields.Count; removeCount++)
            {
                count -= sortedYields[removeCount];
                if (!countMeetsTarget(count) && !shouldRemoveMoreDesignations(designationCount)) break;
                designationCount--;
            }
            return removeCount;
        }

        /// <summary>How many of the (best first) candidates to designate until the count meets the target or the cap is hit.</summary>
        public static int ComputeNumberToDesignate(int startingCount, IReadOnlyList<int> sortedYields, int startingDesignationCount,
            Func<int, bool> countMeetsTarget, Func<int, bool> canAddMoreDesignations)
        {
            int count = startingCount, designationCount = startingDesignationCount, designateCount = 0;
            for (; designateCount < sortedYields.Count; designateCount++)
            {
                if (countMeetsTarget(count) || !canAddMoreDesignations(designationCount)) break;
                count += sortedYields[designateCount];
                designationCount++;
            }
            return designateCount;
        }
    }
}
