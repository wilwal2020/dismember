# Dismember (BONELAB mod)

Slice NPCs at any angle. The blade's actual plane cuts the NPC's real mesh, so you get diagonal torso cuts,
decapitations, split forearms, lopped-off skull tops, and so on. Whether a swing gets through depends on how hard
you swing it.

Requires MelonLoader 0.7 and BoneLib (BoneLib only for the in-game menu).

## Features

- **Arbitrary-angle cuts.** The NPC's skinned meshes are split along the plane of the blade where it hit.
  Severed parts keep their ragdoll physics (an arm keeps its elbow and wrist joints) and can be cut again.
- **Cutting through, not on touch.** When an edge hits flesh hard enough, the blade goes *into* the body. The flesh
  drags on it (you feel it slow down, and the controller buzzes), and a part only comes off once the edge has come
  out the other side. Pull back halfway and you only leave a wound.
- **Force-based.** A swing's power = ½ × (blade mass + 2.5 kg per hand holding it) × edge speed². Flesh costs power
  per cm² of cross-section and each bone adds a fixed cost, so a neck is cheap, a torso is expensive, and a wrist is
  almost free. The blade works through the body parts in the order it reaches them: it can take off the arm in front
  and still get stuck in the torso behind it.
- **Open wounds.** A cut that does not go all the way through leaves an open gash along the line the blade went in
  (skin and fat at the lips, raw flesh down the middle). It grows while you work a stuck blade deeper. Light hits leave
  a small nick.
- **You see the cut being made.** While the blade travels through the body, the cut opens up behind its edge (the
  skin parts, flesh walls inside) and keeps growing as it goes deeper; a part comes off once the edge is out the other
  side, and its cut face takes the opening's place.
- **Blades stay on their line.** While a steel edge is inside, the flesh holds the blade to the line it is cutting
  along: it can go deeper, saw along its edge and follow the swing round, but not twist or slide out of its cut
  unless you wrench it hard (a setting; lightsabers are free so you can steer them).
- **Wounds with depth and stab holes.** A part-way cut stays open in the body's own mesh: the
  skin parts and flesh walls line it as deep as the blade went, so you can look into it. A stab leaves a slot as wide
  as the blade; one that came out the back is a hole you can see through. A later cut through a wound closes it first,
  so the cut face is whole.
- **Stuck blades.** If a swing runs out of power inside the body, or you stop mid-cut, the blade stays stuck in the
  wound. Keep pushing, or **saw back and forth along the edge**, to finish the cut. Pull it back out the way it went
  in to free it, or yank it sideways hard enough to rip it out. Let go and the sword stays in the body.
- **Blade length matters.** Only the edge cuts (plus a little extra reach past the tip, a setting), so a dagger can
  take a hand or an arm off but cannot cut a torso in half. Blades cut edge-first: up to ~35 degrees off still counts
  fully, beyond that the swing loses power, and the flat or the spine (single-edged blades like the katana) does not cut.
- **Swords, knives, axes and cleavers.** Slashing weapons cut with their edges; axes and the cleaver (which the game
  sets up as chopping stab points, not blades) cut with their heads.
- **Lightsabers.** Energy blades (like the Dynamic Lightsaber Pack's, whose blades are triggers that grow when they
  turn on) cut through anything they touch, at any speed, with no drag and no getting stuck. Their cuts are
  cauterised: charred faces with a glowing rim, sparks instead of blood, no bleeding out (a setting). Steer the beam
  while it is in the body and the cut follows the curve (an arc instead of a flat plane). Thrust one straight in and
  it burns a hole.
- **Thrown blades cut too** and keep flying with whatever energy they have left.
- **Blood on the floor.** Spray from cuts and stumps leaves spots where it lands (floors and walls), pooling where it
  keeps dripping and drying away after about 40 s.
- **Cut faces.** The cut is capped with flesh, a skin/fat rim and bone discs where the plane crosses a bone.
  Palettes: flesh, void (Null bodies), robot (Omni/turrets), bone (skeletons). Robots are much harder to cut.
- **Consequences.** Losing the head or the torso kills. Losing a limb cripples that limb. Wounds bleed (pulsing
  arterial spray) and NPCs that have lost limbs bleed out over ~25 s (can be turned off).
- **Sounds.** A wet slice as the blade goes in, a chop when a part comes off, cracks through bone, a thunk when a
  blade sticks, scrapes while sawing. They are synthesised by the mod (no audio files) and play through the game's
  sound-effects mixer.
- **Haptics** on cuts, when a blade sticks and while sawing.
- **Pool-safe.** Every change is recorded and undone when the game reuses an NPC from its pool.
- **Smooth.** The heavy work (5–60 ms per cut) runs on a worker thread; the cut appears a frame or two after the hit.

## Playing

1. Copy `Dismember.dll` into `BONELAB\Mods` (or build with `-p:Deploy=true`, see below).
2. Start the game. The **first launch** indexes the asset bundles in the background: the game's own bundles in a
   few seconds, then mod.io content (a couple of minutes for a few GB). Watch the MelonLoader console for
   `Mesh index ready...`. NPCs can't be cut until the game content is indexed. The index is cached after that.
3. Grab a sword (katana, claymore, axes, knives...) and swing **hard** at an NPC.

BoneMenu → **Dismember** has the settings and a **Testing** page. Its buttons slice the nearest NPC
(neck / random arm / random leg / torso diagonal / skull top), restore it, and print status to the log.

Every setting has a tooltip, and **What do these do?** explains each one in full.

| Setting | Default | What it does |
|---|---|---|
| Cut strength | 1.0 | How easily blades cut. 2 = everything is twice as soft, 0.5 = twice as tough. |
| Arm strength (kg/hand) | 2.5 | How much your arm adds to a held swing. Two hands count twice. |
| Min swing speed | 1.4 m/s | How fast the edge must move before it can cut at all (up to 15; a firm swing is 10-15 m/s). |
| Flesh drag | 1.0 | How much the body slows the blade while cutting. 0 = glides through. |
| Blade guidance | 1.0 | How firmly a cutting blade is held on its line. 0 = free, 2 = firm. |
| Extra blade reach | 8 cm | How far past the tip a cut reaches (longer blades get a bit more). 0 = only the blade. |
| Blades get stuck | on | Weak swings leave the blade stuck. Push or saw to finish it. |
| Stuck blade resistance | 0.6 | How hard it is to push or saw a stuck blade through. |
| Cut through at | 90 % | How deep a cut must get for the part to come off when the blade stops, strays or comes back out. 100 = only all the way. |
| Blade must span | 80 % | How much of a part's width the edge must sweep across for it to come off once through. |
| Open wounds | on | Part-way cuts leave a visible wound. |
| Deep wounds and stab holes | on | Cuts open up as they are made and stay open so you can look in; stabs leave holes. Off: surface only. |
| Sound volume | 1.0 | 0 = no cutting sounds. |
| Lightsabers burn wounds shut | on | Lightsaber cuts are charred, with sparks instead of blood, and do not bleed out. |
| Blood amount | 1.0 | 0 = no blood. |
| NPCs bleed out | on | NPCs that lose a limb die after about half a minute. |
| Parts hit the body | on | Cut-off parts bump into the body (off: they fall through it). |
| Cut face style | Auto | How cut faces are drawn. Try another if they ever look empty. |

Everything is also in `UserData\MelonPreferences.cfg` under `[Dismember]` (plus chunk lifetime, max cuts per NPC
and debug logging).

## How it works

- `src/Dismember/Core`: engine-free slicing. It skins the meshes on the CPU, traces the cross-section outlines,
  groups them into separate sections (an arm, the neck...), decides which ragdoll bodies end up in which piece,
  splits the triangles, rebinds vertices and builds the caps. `CutEnergy` is the force model.
- `src/Dismember/Assets/BundleMeshReader.cs`: BONELAB's NPC meshes are not CPU-readable at runtime, so their
  vertex data is decoded straight from the game's asset bundles (AssetsTools.NET, which ships with MelonLoader).
- `src/Dismember/Game`: the in-game side.
  - `Blades`: weapon contacts (Harmony hooks on `StabSlash`) and the swing energy and plane.
  - `Slices`: a blade travelling through the body (drag, energy, sever on exit, stuck joint).
  - `CutPipeline` / `CutApplier`: the worker thread, and turning a result into new meshes, freed joints,
    trimmed colliders and severed parts.
  - `NpcBody`: the NPC and its undo log.
  - `NpcEffects` and `Gore`: death, crippling, bleeding and blood.
  - `Wounds` and `Sounds`: open wounds from part-way cuts (`Core/GashBuilder` while the blade is in, then opened in
    the mesh by `Core/CutSolver.Slit`), and the cutting sounds (`Core/SoundSynth`).
  - `Stabs`: follows the game's stab joints (and lightsaber thrusts) and leaves a hole when the blade comes out.

## Building and testing

```
dotnet build src/Dismember -c Release                  # -> src/Dismember/bin/Release/net6.0/Dismember.dll
dotnet build src/Dismember -c Release -p:Deploy=true   # also copies it into BONELAB\Mods
dotnet run --project tools/Dismember.Tests -c Release  # offline tests on Ford's real mesh
```

`Directory.Build.props` points at `D:\SteamLibrary\steamapps\common\BONELAB`; override with `-p:BonelabPath=...`.

The test harness loads Ford straight from the game files and runs 10 reference cuts, including the four from
the reference picture, plus 7 swings through the energy model. It checks that no surface area is lost, that bone
weights are valid and that caps sit on the plane. 160 random cuts check that every cut face is filled with no holes
(and does not spill outside the body), that no piece has holes in its skin and that cut faces ride the same bodies
as the skin beside them; the same checks run on 120 curved (lightsaber arc) cuts. A part-way cut checks that the wound
sits on the skin; a belly wound and a chest stab check that you can see into / through them, that the flesh walls close
every opening in the skin, and that a later cut through the wound still gets a whole cut face; a belly chop opened step
by step (as the game shows a cut being made) checks that the opening follows the edge in. The sounds are
checked and written to `out/sounds/*.wav` so you can listen to them. It also writes a 3D viewer of every cut to
`tools/Dismember.Tests/bin/Release/net10.0/out/index.html` (serve that folder over HTTP to view it;
`.claude/launch.json` has a config for it).

## Known limits

- **Still being tuned in VR.** The slicing core is tested on real game data; the feel (power, drag, reach, sound
  levels) is tuned from playtests.
- Only NPCs with a PuppetMaster ragdoll (Ford, Peasants, Null bodies, Omni, skeletons...). Not players or avatars.
  Meshes saved with Unity mesh compression (the female and Null peasants) are decoded too.
- No LabFusion (multiplayer) sync.
- Severed parts use the NPC's highest-detail mesh, and LOD switching is turned off for an NPC once it has been cut.
