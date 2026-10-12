using System;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(Dismember.Game.DismemberMod), "Dismember", "0.1.0", "Dismember")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]
[assembly: MelonOptionalDependencies("BoneLib")]

namespace Dismember.Game
{
    public sealed class DismemberMod : MelonMod
    {
        private bool _menuBuilt;
        private bool _wasEnabled = true;

        public override void OnInitializeMelon()
        {
            Log.Init(LoggerInstance);
            Settings.Init();
            MeshLibrary.Init(MelonEnvironment.UserDataDirectory);
            CutPipeline.Start();
            TryBuildMenu();
            Log.Msg("Dismember loaded.");
        }

        private void TryBuildMenu()
        {
            if (_menuBuilt) return;
            try
            {
                BuildMenu();
                _menuBuilt = true;
            }
            catch (Exception e)
            {
                Log.Debug("BoneMenu not available yet: " + e.Message);
            }
        }

        // Kept separate so a missing BoneLib only fails this call, not the whole mod.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BuildMenu() => Settings.BuildMenu();

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            NpcRegistry.Sweep();
            Gore.OnLevelLoaded();
            Sounds.OnLevelLoaded();
            Wounds.Sweep();
            Stabs.Clear();
            Splats.OnLevelLoaded();
            TryBuildMenu();
        }

        public override void OnUpdate()
        {
            bool enabled = Settings.Enabled.Value;
            if (_wasEnabled && !enabled)
            {
                Slices.ReleaseAll();
                Collisions.ReleaseAll();
            }
            _wasEnabled = enabled;

            CutPipeline.Update();
            Wounds.Update();
            NpcRegistry.Update(Time.deltaTime);
            Gore.Update();
            Splats.Update();
            Cleanup.Update();
        }

        public override void OnFixedUpdate()
        {
            NpcRegistry.FixedUpdate();
            Slices.FixedUpdate();
            Stabs.FixedUpdate();
            Collisions.FixedUpdate();
        }
    }
}
