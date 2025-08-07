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

                patchType = typeof(Building_MassStorageUnit_RegisterNewItem_Patch);
                var prefix = patchType.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
                target = typeof(Building_MassStorageUnit).GetMethod("RegisterNewItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

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
         * This works? All that needed to be done is to despawn the item and respawn it on the right map.
         */
        public static class Building_MassStorageUnit_RegisterNewItem_Patch
        {
            private static Lazy<FieldInfo> _ItemsField = new(() => AccessTools.Field(typeof(Building_MassStorageUnit), "items"));
            private static List<Thing> getItems(Building_MassStorageUnit instance)
            {
                return (List<Thing>)_ItemsField.Value.GetValue(instance);
            }

            public static bool Prefix(Building_MassStorageUnit __instance, Thing newItem)
            {
                __instance.ItemCountsAdded(newItem.def, newItem.stackCount);
                List<Thing> thingList = __instance.Position.GetThingList(__instance.Map);
                for (int i = 0; i < thingList.Count; i++)
                {
                    Thing thing = thingList[i];
                    bool flag = thing == newItem;
                    if (!flag)
                    {
                        bool flag2 = thing.def.category == ThingCategory.Item && thing.CanStackWith(newItem);
                        if (flag2)
                        {
                            thing.TryAbsorbStack(newItem, true);
                        }
                        bool destroyed = newItem.Destroyed;
                        if (destroyed)
                        {
                            return false;
                        }
                    }
                }
                bool destroyed2 = newItem.Destroyed;
                if (destroyed2)
                {
                    return false;
                }

                if (!getItems(__instance).Contains(newItem))
                {
                    getItems(__instance).Add(newItem);
                }
                if (newItem.Spawned && newItem.Map != __instance.Map) { 
                    newItem.DeSpawn(DestroyMode.Vanish);
                }
                if (!newItem.Spawned)
                {
                    newItem.SpawnSetup(__instance.Map, false);
                }
                if (__instance.CanStoreMoreItems)
                {
                    newItem.Position = __instance.Position;
                }

                return false; // Skip original method
            }
        }
    }
}