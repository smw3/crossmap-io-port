using HarmonyLib;
using ProjectRimFactory.Storage;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Assertions;
using Verse;
using Verse.AI;
using static UnityEngine.Scripting.GarbageCollector;

namespace CrossmapPorts
{
    static class CrossmapPortsHarmony
    {
        public static bool MatchOpCodeSequence(List<CodeInstruction> instructions, int startIndex, List<OpCode> pattern)
        {
            if (startIndex < 0 || startIndex + pattern.Count > instructions.Count)
                return false;

            for (int i = 0; i < pattern.Count; i++)
            {
                if (instructions[startIndex + i].opcode != pattern[i])
                    return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll))]
        public static class StaticConstructorOnStartupUtility_CallAll_Patch
        {
            public static void Postfix()
            {
                // Fire off the actual patches AFTER StaticConstructorOnStartupUtility.CallAll has run. Otherwise the classes attempt to load textures etc too early and icons break. The concept seems fine.
                var harmony = new Harmony("CrossmapPorts.Late");

                Log.Message("CrossmapPorts: Late-patching");

                var patchType = typeof(Building_StorageUnitIOBase_GetGizmos_Patch);
                var transpiler = patchType.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
                var target = typeof(Building_StorageUnitIOBase).GetMethod("<GetGizmos>b__56_0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));

                patchType = typeof(Building_ColdStorage_RegisterNewItem_Patch);
                var prefix = patchType.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
                target = typeof(Building_ColdStorage).GetMethod("HandleNewItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            }
        }

        /*
         * This works! Just replaces the gizmo filter so it looks over all maps. 
         */
        public static class Building_StorageUnitIOBase_GetGizmos_Patch
        {
            public static List<Building> GetAllIOPortsAcrossMaps()
            {
                IEnumerable<Building> allBuildings = Find.Maps.SelectMany(map => map.listerBuildings.allBuildingsColonist);
                return allBuildings
                    .Where((Building b) => b is ILinkableStorageParent && (b as ILinkableStorageParent).CanUseIOPort)
                    .ToList();
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var patched = false;
                List<OpCode> targetCodes = [OpCodes.Nop, OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Ldfld, OpCodes.Ldfld, OpCodes.Ldsfld, OpCodes.Dup, OpCodes.Brtrue_S, OpCodes.Pop, OpCodes.Ldsfld, OpCodes.Ldftn, OpCodes.Newobj, OpCodes.Dup, OpCodes.Stsfld, OpCodes.Call, OpCodes.Call, OpCodes.Stloc_0];

                Log.Message("CrossmapPorts: Attempting to patch Building_StorageUnitIOBase.GetGizmos to include all IOPorts across maps.");
                for (var i = 0; i < codes.Count; i++)
                {
                    if (MatchOpCodeSequence(codes, i, targetCodes))
                    {
                        // Replace the sequence with a call to a new method
                        var replacementMethod = AccessTools.Method(typeof(Building_StorageUnitIOBase_GetGizmos_Patch), nameof(GetAllIOPortsAcrossMaps));
                        yield return new CodeInstruction(OpCodes.Callvirt, replacementMethod);
                        yield return new CodeInstruction(OpCodes.Stloc_0);

                        i += targetCodes.Count; // Skip the next few instructions that were replaced
                        patched = true;
                    }

                    yield return codes[i];
                }

                if (!patched)
                {
                    Log.Error("CrossmapPorts: Failed to patch Building_StorageUnitIOBase.GetGizmos. The method may have changed.");
                } else
                {
                    Log.Message("CrossmapPorts: Successfully patched Building_StorageUnitIOBase.GetGizmos to include all IOPorts across maps.");
                }
            }
        }

        /*
         * This does NOT work. You PROBABLY need to patch Building_ColdStorage.HandleNewItem because otherwise it just moves items to a different location on the IO port's map, rather than to the map the storage is on.
         */
        public static class Building_ColdStorage_RegisterNewItem_Patch
        {
            private static Lazy<PropertyInfo> _ItemsProperty = new(() => AccessTools.Property(typeof(Building_ColdStorage), "Items"));
            private static List<Thing> getItems(Building_ColdStorage instance)
            {
                    return (List<Thing>)_ItemsProperty.Value.GetValue(instance);
            }

            private static Lazy<FieldInfo> _ThingOwner = new (() => AccessTools.Field(typeof(Building_ColdStorage), "thingOwner"));

            public static bool Prefix(Building_ColdStorage __instance, Thing item)
            {              
                Log.Message($"CrossmapPorts: RegisterNewItem called for {item} in {__instance}");
                if (getItems(__instance).Contains(item))
                {
                    Log.Message(string.Format("dup: {0}", item));
                }
                else
                {
                    foreach (Thing thing in getItems(__instance).ToArray())
                    {
                        bool flag2 = thing.def.category == ThingCategory.Item;
                        if (flag2)
                        {
                            thing.TryAbsorbStack(item, true);
                        }
                        bool destroyed = item.Destroyed;
                        if (destroyed)
                        {
                            break;
                        }
                    }
                    if (!item.Destroyed)
                    {
                        ThingOwner holdingOwner = item.holdingOwner;
                        if (holdingOwner != null)
                        {
                            holdingOwner.Remove(item);
                        }
                        ((ThingOwner<Thing>)(_ThingOwner.Value.GetValue(__instance))).TryAdd(item, false);
                        bool canStoreMoreItems = __instance.CanStoreMoreItems;
                        // Changes after this comment, but they don't work since the entire function doesn't seem to actually be called.
                        if (item.Spawned)
                        {
                            Log.Message($"Despawning item {item}");
                            item.DeSpawn(DestroyMode.Vanish);
                        }                        
                        if (canStoreMoreItems)
                        {
                            var oldMap = item.Map;
                            item.SpawnSetup(__instance.Map, true);
                            Log.Message($"Moved item, old map {oldMap} new map {item.Map}");
                        }
                    }
                }

                return false; // Skip original method
            }
        }
    }
}