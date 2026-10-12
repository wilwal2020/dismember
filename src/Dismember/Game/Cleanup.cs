using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dismember.Game
{
    /// <summary>Delayed actions and object expiry, driven from the mod's Update.</summary>
    internal static class Cleanup
    {
        private static readonly List<(float at, Action action)> Pending = new List<(float, Action)>();
        private static readonly List<(float at, GameObject go)> Expiring = new List<(float, GameObject)>();

        public static void After(float seconds, Action action) => Pending.Add((Time.time + seconds, action));

        public static void Expire(GameObject go, float seconds)
        {
            if (seconds > 0f) Expiring.Add((Time.time + seconds, go));
        }

        public static void Update()
        {
            float now = Time.time;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Pending[i].at > now) continue;
                var a = Pending[i].action;
                Pending.RemoveAt(i);
                try { a(); }
                catch (Exception e) { Log.Debug("delayed action failed: " + e.Message); }
            }
            for (int i = Expiring.Count - 1; i >= 0; i--)
            {
                if (Expiring[i].at > now) continue;
                var go = Expiring[i].go;
                Expiring.RemoveAt(i);
                if (!go.IsDead()) UnityEngine.Object.Destroy(go);
            }
        }

        public static void Clear()
        {
            Pending.Clear();
            Expiring.Clear();
        }
    }
}
