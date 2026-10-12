using System;
using HarmonyLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.AI;
using UnityEngine;

namespace Dismember.Game
{
    // Hooks into the game. MelonLoader applies every [HarmonyPatch] class in this assembly at startup.
    // Exceptions must never escape into IL2CPP code, so every hook swallows and logs them.

    /// <summary>Every weapon's motion right before the physics step, i.e. before a hit slows it down.</summary>
    [HarmonyPatch(typeof(StabSlash), nameof(StabSlash.FixedUpdate))]
    internal static class StabSlashFixedUpdatePatch
    {
        private static void Prefix(StabSlash __instance)
        {
            try { Blades.Record(__instance); }
            catch (Exception e) { Log.Debug("record: " + e.Message); }
        }
    }

    /// <summary>
    /// Weapon contacts. Runs after the game's own stab/slash handling, so its sounds, damage and stabbing still
    /// happen; a swing that cuts or sticks then takes over from the game's slash joints.
    /// </summary>
    [HarmonyPatch(typeof(StabSlash), nameof(StabSlash.OnCollisionEnter))]
    internal static class StabSlashCollisionEnterPatch
    {
        private static void Postfix(StabSlash __instance, Collision c)
        {
            try { Blades.OnContact(__instance, c); }
            catch (Exception e) { Log.Debug("contact: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(StabSlash), nameof(StabSlash.OnCollisionStay))]
    internal static class StabSlashCollisionStayPatch
    {
        private static void Postfix(StabSlash __instance, Collision c)
        {
            try { Blades.OnContact(__instance, c); }
            catch (Exception e) { Log.Debug("contact: " + e.Message); }
        }
    }

    /// <summary>NPCs come out of a pool: anything we cut in a previous life is put back.</summary>
    [HarmonyPatch(typeof(AIBrain), nameof(AIBrain.OnPoolInitialize))]
    internal static class AIBrainSpawnPatch
    {
        private static void Postfix(AIBrain __instance)
        {
            try { NpcRegistry.OnSpawn(__instance); }
            catch (Exception e) { Log.Debug("spawn: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(AIBrain), nameof(AIBrain.OnPoolDeInitialize))]
    internal static class AIBrainDespawnPatch
    {
        private static void Prefix(AIBrain __instance)
        {
            try { NpcRegistry.OnDespawn(__instance); }
            catch (Exception e) { Log.Debug("despawn: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(AIBrain), nameof(AIBrain.OnResurrection))]
    internal static class AIBrainResurrectPatch
    {
        private static void Postfix(AIBrain __instance)
        {
            try { NpcRegistry.OnSpawn(__instance); }
            catch (Exception e) { Log.Debug("resurrect: " + e.Message); }
        }
    }
}
