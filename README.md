<img width="1353" height="835" alt="Injector3" src="https://github.com/user-attachments/assets/0c0e4950-7023-4921-a627-78eb869784f1" />
<img width="1357" height="765" alt="Injector2" src="https://github.com/user-attachments/assets/70b491d2-5ed3-49a3-8a52-921c8094b7bb" />

A loadout injection framework for Nuclear Option. Allows user to freely override and customize aircraft loadouts via JSON configuration files, eliminating the need for hardcoded dependencies and manual prefab editing.

## Tutorial (Updated for 0.34.2 / v2.0.0)

### Step 0 — Let the mod generate your files

Install to the `plugins` folder along with any modded aircraft and weapon packs, then go to Free Flight and preview every aircraft by clicking each icon in turn.

This matters: the mod writes an aircraft's files the **first time it sees that aircraft**. An aircraft you have never previewed has no folder yet. Previewing everything once creates the lot.

Everything lives in `BepInEx/plugins/preset-loadout/`.

### Step 1 — Find your aircraft's folder

Folder names are the game's internal names, not the ones on screen — the Vagrant is `VTOLTrainer1`.

The quickest way to map them is `preset-loadout/hardpointdictionary.log`. Its second section lists every aircraft, every weapon station and which mod added it:

```
[B] HARDPOINT SETS (weapon stations)
VTOLTrainer1	0	Center Pylon	-	1	1,2	vanilla
VTOLTrainer1	1	Left Fuselage Pylon	-	1	0	vanilla
VTOLTrainer1	2	Right Fuselage Pylon	Fuselage Pylons	1	0	vanilla
VTOLTrainer1	3	Inner wing pylons	-	2	-	vanilla
```

The columns are: aircraft, station number, station name, paired name, how many hardpoints the station has, which stations it **precludes**, and the mod that added it.

That precludes column is worth knowing. On the Vagrant, station 0 (Center Pylon) precludes `1,2`, so loading the centre pylon forces both fuselage pylons empty and vice versa — that is the game's own rule, not something this mod does.

### Step 2 — Pick the weapon station

Inside the aircraft folder there is one `weaponstation<number>` folder per station, numbered from 0 in the game's own station order (left to right). **You do not have to count pylons** — each folder already contains a JSON file named after the station, so just read the names:

```
VTOLTrainer1/
  weaponstation0/Center Pylon.json
  weaponstation1/Left Fuselage Pylon.json
  weaponstation2/Right Fuselage Pylon.json
  weaponstation3/Inner wing pylons.json
  weaponstation4/Outer wing pylons.json
  weaponstation5/Wingtip Pylons.json
```

The files are created for you. You never need to add one by hand — open the one that is already there.

### Step 3 — Find the weapon's name

Open `preset-loadout/hardpointdictionary.log`. Every mount is one row:

```
owner jsonKeydisplayNameassetNameammo  note
vanilla  AAM1_double  MMR-S3 x2  AAM1_double 2
vanilla  AShM2_internalx8AGM-99 x6  AShM2_internalx6  6  STALE-KEY(jsonKey says 8, real 6 - using assetName)
```

- `displayName` is what you see in the hangar, so use it to find the weapon you actually want.
- **Paste the `jsonKey` column** into the JSON.
- **Except on `STALE-KEY` rows** — there the developers changed the weapon but kept the old jsonkey, so  the name lies about the count.
    - Paste the `assetName` instead. In the row above the mount is called `...x8` but really carries 6, so you would write `AShM2_internalx6`.
- `owner` tells you which mod shipped the weapon.

### Step 4 — Edit the list

`weaponstation5/Wingtip Pylons.json`, before:

```json
{
 "allowedWeapons": [
  "AAM1_single",
  "AAM2_single",
  "Aryx_ExternalFlareLauncher16"
 ]
}
```

After, adding a weapon:

```json
{
 "allowedWeapons": [
  "AAM1_double",
  "AAM1_single",
  "AAM2_single",
  "Aryx_ExternalFlareLauncher16"
 ]
}
```

Every line needs a trailing comma except the last one. Adding your new weapon at the top or middle is the easiest way to avoid getting that wrong.

Changes are picked up next time the aircraft loads — leave the hangar and come back, or restart. (UI still WIP)

### Important: the list is now a whitelist

**This changed in 2.0.0.** The file is the complete definition of that station:

- a weapon **listed** is available;
- a weapon **not listed** is removed and cannot be loaded by anything — not the hangar, not a saved preset, not a mission file, not AI.

So do **not** delete the weapons that were already in the file unless you actually want them gone.

If you only ever want to add and never remove, set `Enforcement / Strict Station Whitelist` to `false` in the config, and the file goes back to being additive only.

### Saving loadouts

Open the loadout screen and you get a **Loadout Presets** panel.

- Whatever you last equipped is saved automatically as `DEFAULT` and put back the next time you open the hangar — so after ejecting you can respawn with the same loadout immediately.
- `Edit` lets you `Add`, `Overwrite`, `Rename` and `Delete` named presets. Fuel and livery are saved along with the weapons.
- `Dump Vanilla` writes out the game's own AI loadouts as presets. They can be edited but not deleted, so the originals are always recoverable.
- Some aircrafts does not have a prebuilt preset, relying on `SelectAIAircraftWeapon`. It is recommended that you make a preset for them.

### AI

AI aircraft draw from the same presets — the game's built-in loadouts, your edits to them, and any you created — while still obeying mission rules on restricted and nuclear weapons.
> Editing a preset changes what the enemy flies too.

To make AI use your injected weapons on every airframe, set `Enforcement / AI Always Randomises Loadout` to `true`.
Note this discards the authored loadouts and their fuel settings.

### Troubleshooting

- **A weapon is missing from the dropdown.** Check it is spelled exactly as in the dictionary, and that the mission has not restricted it — missions can ban weapons, and they do so by the `assetName` column.
- **A preset loads with an empty station.** That weapon is unavailable right now: removed from the  station's JSON, or restricted by the mission. Turn on `Debug / Verbose Logging` and the log names the station and the reason. Your preset file is not modified, so it comes back in a mission that allows it.
- **Files look out of date.** Delete `preset-loadout/.schema-version` and relaunch to regenerate the station files. Deleting a whole aircraft folder regenerates it from scratch on next preview.
- **Nothing generated for an aircraft.** You have not previewed it yet — see Step 0.

## Features

- **Per-station JSON allow-lists.** Each aircraft gets `preset-loadout/<aircraft>/weaponstation<i>/<station>.json`  listing the mounts that station accepts. Files are generated lazily the first time an aircraft is seen, so modded airframes seed themselves with no manual setup.
- **The JSON is authoritative.** A mount listed is added; a mount absent is removed and cannot be loaded by any route — hangar UI, saved presets, mission JSON, standard loadouts or AI. Enforcement sits at the single point every spawn passes through, so nothing slips past it.
- **Hardpoint dictionary.** `preset-loadout/hardpointdictionary.log` lists every weapon mount with its owner, jsonKey, in-game display name, asset name, real ammo count, and a note column. Mounts whose jsonKey advertises a count the mount no longer carries are flagged `STALE-KEY`
  - for example `AShM2_internalx8` actually holds 6 — and the accurate spelling is used automatically. A second section lists every weapon station with its name, hardpoint count and exclusions.
- **Mod attribution.** Each mount and station is traced to the asset bundle that shipped it, so the dictionary tells you which mod contributed what.
- **Persistent loadout presets.** Per-aircraft `.preset` files live alongside that aircraft's station JSONs. A `DEFAULT` preset is auto-saved whenever you change a weapon and re-applied when the hangar loads, carrying weapons, fuel and livery. Named presets can be added, overwritten,renamed and deleted from the in-hangar panel.
- **Vanilla AI loadouts, dumped and editable.** Each aircraft's authored AI loadouts are written out as presets the first time that aircraft is seen. They can be edited freely but never deleted, so the game's own loadouts are always recoverable.
- **AI flies your presets.** AI aircraft draw from one pool of vanilla loadouts, your edits to them, and presets you created, under the same mission rules vanilla applies — restricted weapons, nuclear release and warhead budget all still gate the choice, and the per-spawn variety is preserved rather than collapsing onto a single loadout.
- **Safe against stale entries.** A preset naming a mount that has been removed from a station, or banned by the current mission, is handled deliberately:
  - the player's station is left empty with the reason reported, and the preset is dropped from the AI pool.
  - Preset files are never rewritten
  behind your back, so a weapon banned in one mission returns in the next.
