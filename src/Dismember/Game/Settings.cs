using System;
using MelonLoader;
using UnityEngine;

namespace Dismember.Game
{
    internal static class Settings
    {
        private static MelonPreferences_Category _cat;

        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<float> CutStrength;
        public static MelonPreferences_Entry<float> HandStrengthKg;
        public static MelonPreferences_Entry<float> MinCutSpeed;
        public static MelonPreferences_Entry<float> SliceDrag;
        public static MelonPreferences_Entry<float> BladeGuide;
        public static MelonPreferences_Entry<bool> StuckBlades;
        public static MelonPreferences_Entry<float> PushResistance;
        public static MelonPreferences_Entry<float> ThroughAtPercent;
        public static MelonPreferences_Entry<float> SpanPercent;
        public static MelonPreferences_Entry<float> BloodAmount;
        public static MelonPreferences_Entry<bool> BleedDamage;
        public static MelonPreferences_Entry<bool> SeveredCollideWithBody;
        public static MelonPreferences_Entry<float> ChunkLifetime;
        public static MelonPreferences_Entry<int> MaxCutsPerNpc;
        public static MelonPreferences_Entry<bool> DebugLog;
        public static MelonPreferences_Entry<int> CapStyle;
        public static MelonPreferences_Entry<float> BladeReachCm;
        public static MelonPreferences_Entry<bool> ShowWounds;
        public static MelonPreferences_Entry<bool> DeepWounds;
        public static MelonPreferences_Entry<float> SoundVolume;
        public static MelonPreferences_Entry<bool> SaberSears;

        // One place for every setting's explanation: used for the config file, BoneMenu tooltips and the help page.
        private const string EnabledHelp = "Turns cutting on or off. Off: weapons behave like in the normal game.";
        private const string CutStrengthHelp =
            "How easily blades cut. 1 = normal. 2 = everything cuts like it is twice as soft (a medium swing takes a head off). " +
            "0.5 = twice as tough (you need really hard swings). Also scales how much the flesh slows the blade.";
        private const string HandStrengthHelp =
            "How much your arm adds to a swing, in kg per hand holding the weapon. A swing's power is the blade's weight plus this, " +
            "times how fast the edge moves. Higher = held swings cut deeper; two-handed swings get it twice. Thrown weapons only use their own weight.";
        private const string MinCutSpeedHelp =
            "How fast the edge has to move (metres per second, where it hits) before it can cut at all. Slower touches are left " +
            "to the game's normal slashing. A gentle swing moves the edge about 4-6 m/s, a firm one 10-15, a full swing 20 or " +
            "more (the speed of every cut is in the MelonLoader log). Lower it if gentle slices should cut, raise it (up to 15) " +
            "if light swings cut too easily.";
        private const string SliceDragHelp =
            "How much the body slows the blade while it is cutting through. 0 = no drag (the blade glides through), " +
            "1 = normal, 2 = heavy. This is what makes cutting through a torso feel different from a wrist.";
        private const string GuideHelp =
            "How firmly the flesh holds a blade to the line it is cutting along. While the edge is inside, the blade keeps " +
            "going the way it went in: it can go deeper, saw along its edge and follow the swing round, but not twist or slide " +
            "sideways out of its cut unless you wrench it hard. 0 = off (the blade moves freely inside), 1 = normal, 2 = firm. " +
            "Lightsabers are never held, so you can steer them along a curve.";
        private const string StuckBladesHelp =
            "If a swing runs out of power inside the body (or you stop mid-cut), the blade stays stuck in the wound. " +
            "Push it deeper or saw it back and forth along the edge to finish the cut, or pull it back out the way it went in. " +
            "Off: weak swings just stop cutting and slide out.";
        private const string PushResistanceHelp =
            "How hard it is to finish a cut with a stuck blade. Low = a little pushing or a few saw strokes is enough. " +
            "High = you really have to work it through.";
        private const string ThroughHelp =
            "How far through a body part (front to back, the way the blade is going) a cut has to get before the part comes off " +
            "when the blade stops, strays off its line, gets stuck and is pulled or yanked back out. 100 = only when the edge " +
            "comes out the other side. 90 = normal: the last tenth tears. Lower = parts come off from shallower cuts. A swing " +
            "that still had the power to finish also counts from 20 below this (except at 100, or when pulled back out).";
        private const string SpanHelp =
            "How much of a body part's width the edge has to sweep across for the part to come off once the blade has gone all " +
            "the way through it. 80 = normal. Lower = hitting with the tip, or a blade shorter than the part, still takes it off. " +
            "100 = the edge has to cover all of it. When it falls short, the part stays on with an open wound.";
        private const string BloodHelp = "How much blood sprays from cuts and stumps. 0 = no blood at all.";
        private const string BleedHelp = "NPCs that lose a limb keep bleeding and die after a while (about half a minute).";
        private const string CollideHelp =
            "Cut-off parts bump into the body they came from. Off: they fall straight through it.";
        private const string ChunkHelp = "Seconds before loose slices (like the top of a skull) disappear. 0 = never.";
        private const string MaxCutsHelp = "Each NPC stops being cuttable after this many cuts (keeps the game fast).";
        private const string CapStyleHelp =
            "How the flesh on a cut face is drawn. Auto: the game's own lit shader (looks like the rest of the body). " +
            "NPC shader: a copy of the NPC's skin material. Plain lit: Unity's standard lit material. Unlit: always bright, " +
            "ignores lighting. Try another one if cut faces look empty or black.";
        private const string ReachHelp =
            "How far past the tip of the blade a cut reaches, in cm (longer blades get a little extra on top). The real edge is " +
            "often a bit short to get through a whole arm or torso when you hit with the end of it. 0 = only the blade itself.";
        private const string WoundsHelp =
            "Cuts that do not go all the way through leave an open wound along the line the blade went in.";
        private const string DeepWoundsHelp =
            "Once the blade is out, a cut that did not go all the way through stays open: the skin parts and you can look into " +
            "it, as deep as the blade went. A stab leaves a hole, and one that went all the way through is a hole you can see " +
            "through. Off: wounds are only drawn on the surface (a little faster).";
        private const string SoundHelp = "Volume of the slicing, chopping and bone sounds. 0 = silent.";
        private const string SaberHelp =
            "Lightsabers (and other energy blades) cut through anything they touch, at any speed. With this on their cuts " +
            "are cauterised: charred cut faces with a glowing edge, sparks instead of blood, and no bleeding out.";
        private const string DebugHelp = "Writes details about every swing (speed, power, what it hit) to the MelonLoader log.";

        public static void Init()
        {
            _cat = MelonPreferences.CreateCategory("Dismember");
            Enabled = _cat.CreateEntry("Enabled", true, "Enabled", EnabledHelp);
            CutStrength = _cat.CreateEntry("CutStrength", 1f, "Cut strength", CutStrengthHelp);
            HandStrengthKg = _cat.CreateEntry("HandStrengthKg", 2.5f, "Arm strength (kg per hand)", HandStrengthHelp);
            MinCutSpeed = _cat.CreateEntry("MinCutSpeed", 1.4f, "Minimum swing speed (m/s)", MinCutSpeedHelp);
            SliceDrag = _cat.CreateEntry("SliceDrag", 1f, "Flesh drag", SliceDragHelp);
            BladeGuide = _cat.CreateEntry("BladeGuide", 1f, "Blade guidance", GuideHelp);
            StuckBlades = _cat.CreateEntry("StuckBlades", true, "Blades get stuck", StuckBladesHelp);
            PushResistance = _cat.CreateEntry("PushResistance", 0.6f, "Stuck blade resistance", PushResistanceHelp);
            ThroughAtPercent = _cat.CreateEntry("ThroughAtPercent", 90f, "Cut through at (%)", ThroughHelp);
            SpanPercent = _cat.CreateEntry("SpanPercent", 80f, "Blade must span (%)", SpanHelp);
            BloodAmount = _cat.CreateEntry("BloodAmount", 1f, "Blood amount", BloodHelp);
            BleedDamage = _cat.CreateEntry("BleedDamage", true, "NPCs bleed out", BleedHelp);
            SeveredCollideWithBody = _cat.CreateEntry("SeveredCollideWithBody", true, "Cut-off parts collide with the body", CollideHelp);
            ChunkLifetime = _cat.CreateEntry("ChunkLifetime", 90f, "Loose slice lifetime (s)", ChunkHelp);
            MaxCutsPerNpc = _cat.CreateEntry("MaxCutsPerNpc", 12, "Max cuts per NPC", MaxCutsHelp);
            DebugLog = _cat.CreateEntry("DebugLog", false, "Debug logging", DebugHelp);
            CapStyle = _cat.CreateEntry("CapStyle", 0, "Cut face style (0 auto, 1 NPC shader, 2 plain lit, 3 unlit)", CapStyleHelp);
            BladeReachCm = _cat.CreateEntry("BladeReachCm", 8f, "Extra blade reach (cm)", ReachHelp);
            ShowWounds = _cat.CreateEntry("Wounds", true, "Open wounds", WoundsHelp);
            DeepWounds = _cat.CreateEntry("DeepWounds", true, "Deep wounds and stab holes", DeepWoundsHelp);
            SoundVolume = _cat.CreateEntry("SoundVolume", 1f, "Sound volume", SoundHelp);
            SaberSears = _cat.CreateEntry("SaberSears", true, "Lightsabers burn wounds shut", SaberHelp);
            Apply();
        }

        public static void Apply()
        {
            Log.Verbose = DebugLog.Value;
            Gore.Amount = BloodAmount.Value;
        }

        private static void Save()
        {
            Apply();
            try { _cat.SaveToFile(false); } catch { }
        }

        /// <summary>Settings page in BoneLib's BoneMenu (optional dependency).</summary>
        public static void BuildMenu()
        {
            var page = BoneLib.BoneMenu.Page.Root.CreatePage("Dismember", new Color(0.85f, 0.1f, 0.12f), 0, true);
            var white = Color.white;

            page.CreateBool("Enabled", white, Enabled.Value, v => { Enabled.Value = v; Save(); }).SetTooltip(EnabledHelp);
            page.CreateFloat("Cut strength", white, CutStrength.Value, 0.25f, 0.25f, 8f, v => { CutStrength.Value = v; Save(); })
                .SetTooltip("1 = normal. Higher = cuts more easily.");
            page.CreateFloat("Arm strength (kg/hand)", white, HandStrengthKg.Value, 0.5f, 0f, 10f, v => { HandStrengthKg.Value = v; Save(); })
                .SetTooltip("How much your arm adds to a held swing.");
            page.CreateFloat("Min swing speed (m/s)", white, MinCutSpeed.Value, 0.5f, 0.5f, 15f, v => { MinCutSpeed.Value = v; Save(); })
                .SetTooltip("Edge speed needed before a hit can cut.");
            page.CreateFloat("Flesh drag", white, SliceDrag.Value, 0.25f, 0f, 3f, v => { SliceDrag.Value = v; Save(); })
                .SetTooltip("How much the body slows the blade. 0 = none.");
            page.CreateFloat("Blade guidance", white, BladeGuide.Value, 0.25f, 0f, 3f, v => { BladeGuide.Value = v; Save(); })
                .SetTooltip("How firmly a cutting blade is held on its line. 0 = free.");
            page.CreateFloat("Extra blade reach (cm)", white, BladeReachCm.Value, 2f, 0f, 30f, v => { BladeReachCm.Value = v; Save(); })
                .SetTooltip("How far past the tip a cut reaches.");
            page.CreateBool("Blades get stuck", white, StuckBlades.Value, v => { StuckBlades.Value = v; Save(); })
                .SetTooltip("Weak swings leave the blade stuck: push or saw to finish.");
            page.CreateFloat("Stuck blade resistance", white, PushResistance.Value, 0.1f, 0.1f, 3f, v => { PushResistance.Value = v; Save(); })
                .SetTooltip("How hard it is to push/saw a stuck blade through.");
            page.CreateFloat("Cut through at (%)", white, ThroughAtPercent.Value, 5f, 50f, 100f, v => { ThroughAtPercent.Value = v; Save(); })
                .SetTooltip("How deep a cut must get for the part to come off when the blade stops or comes back out. 100 = all the way.");
            page.CreateFloat("Blade must span (%)", white, SpanPercent.Value, 5f, 10f, 100f, v => { SpanPercent.Value = v; Save(); })
                .SetTooltip("How much of a part's width the edge must cross for it to come off.");
            page.CreateBool("Open wounds", white, ShowWounds.Value, v => { ShowWounds.Value = v; Save(); })
                .SetTooltip("Part-way cuts leave a visible wound.");
            page.CreateBool("Deep wounds and stab holes", white, DeepWounds.Value, v => { DeepWounds.Value = v; Save(); })
                .SetTooltip("Wounds open up so you can look into them; stabs leave holes.");
            page.CreateFloat("Sound volume", white, SoundVolume.Value, 0.25f, 0f, 2f, v => { SoundVolume.Value = v; Save(); })
                .SetTooltip("0 = no cutting sounds.");
            page.CreateBool("Lightsabers burn wounds shut", white, SaberSears.Value, v => { SaberSears.Value = v; Save(); })
                .SetTooltip("Lightsaber cuts are charred, with sparks instead of blood.");
            page.CreateFloat("Blood amount", white, BloodAmount.Value, 0.25f, 0f, 4f, v => { BloodAmount.Value = v; Save(); })
                .SetTooltip("0 = no blood.");
            page.CreateBool("NPCs bleed out", white, BleedDamage.Value, v => { BleedDamage.Value = v; Save(); })
                .SetTooltip("NPCs that lose a limb die after a while.");
            page.CreateBool("Parts hit the body", white, SeveredCollideWithBody.Value, v => { SeveredCollideWithBody.Value = v; Save(); })
                .SetTooltip("Cut-off parts bump into the body (off: fall through).");
            page.CreateEnum("Cut face style", white, (Gore.CapStyle)Math.Clamp(CapStyle.Value, 0, 3), v => { CapStyle.Value = (int)(Gore.CapStyle)v; Save(); })
                .SetTooltip("How cut faces are drawn. Try another if they look empty.");

            var help = page.CreatePage("What do these do?", new Color(0.55f, 0.8f, 1f), 0, true);
            void Explain(string name, string text) =>
                help.CreateFunction(name, white, () => BoneLib.BoneMenu.Menu.DisplayDialog(name, text, null, null, null)).SetTooltip(text);
            Explain("How cutting works",
                "Swing a blade edge-first into an NPC. The faster the edge moves and the heavier the swing, the further it gets: " +
                "it goes into the body, the flesh slows it down, and a part only comes off once the edge comes out the other side. " +
                "Necks and wrists are easy, torsos need a big swing. Run out of power inside and the blade is stuck: push or saw to finish, pull back to free it.");
            Explain("Cut strength", CutStrengthHelp);
            Explain("Arm strength", HandStrengthHelp);
            Explain("Min swing speed", MinCutSpeedHelp);
            Explain("Flesh drag", SliceDragHelp);
            Explain("Blade guidance", GuideHelp);
            Explain("Extra blade reach", ReachHelp);
            Explain("Blades get stuck", StuckBladesHelp);
            Explain("Stuck blade resistance", PushResistanceHelp);
            Explain("Cut through at", ThroughHelp);
            Explain("Blade must span", SpanHelp);
            Explain("Open wounds", WoundsHelp);
            Explain("Deep wounds and stab holes", DeepWoundsHelp);
            Explain("Sound volume", SoundHelp);
            Explain("Lightsabers", SaberHelp);
            Explain("Blood amount", BloodHelp);
            Explain("NPCs bleed out", BleedHelp);
            Explain("Parts hit the body", CollideHelp);
            Explain("Cut face style", CapStyleHelp);

            var debug = page.CreatePage("Testing", Color.yellow, 0, true);
            debug.CreateFunction("Slice nearest: neck", Color.yellow, () => DebugTools.SliceNearest(DebugTools.Preset.Neck))
                .SetTooltip("Cuts the nearest NPC without a swing.");
            debug.CreateFunction("Slice nearest: random arm", Color.yellow, () => DebugTools.SliceNearest(DebugTools.Preset.Arm));
            debug.CreateFunction("Slice nearest: random leg", Color.yellow, () => DebugTools.SliceNearest(DebugTools.Preset.Leg));
            debug.CreateFunction("Slice nearest: torso diagonal", Color.yellow, () => DebugTools.SliceNearest(DebugTools.Preset.TorsoDiagonal));
            debug.CreateFunction("Slice nearest: top of skull", Color.yellow, () => DebugTools.SliceNearest(DebugTools.Preset.SkullTop));
            debug.CreateFunction("Restore nearest NPC", Color.green, DebugTools.RestoreNearest).SetTooltip("Puts the nearest NPC back together.");
            debug.CreateFunction("Rebuild mesh index", Color.cyan, () => MeshLibrary.StartIndexBuild(true))
                .SetTooltip("Only needed after installing new NPC mods.");
            debug.CreateFunction("Print status to log", Color.cyan, DebugTools.PrintStatus);
            debug.CreateBool("Debug logging", white, DebugLog.Value, v => { DebugLog.Value = v; Save(); }).SetTooltip(DebugHelp);
        }
    }
}
