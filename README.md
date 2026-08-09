A loadout injection framework for Nuclear Option. Allows user to freely override and customize aircraft loadouts via JSON configuration files, eliminating the need for hardcoded dependencies and manual prefab editing.
<img width="1353" height="835" alt="Injector3" src="https://github.com/user-attachments/assets/0c0e4950-7023-4921-a627-78eb869784f1" />
<img width="1357" height="765" alt="Injector2" src="https://github.com/user-attachments/assets/70b491d2-5ed3-49a3-8a52-921c8094b7bb" />

## Tutorial (Updated for 0.34.1)
Step 0: Install to plugins folder along with all other modded aircraft & weapon, go to free flight and preview all aircraft (clicking the aircraft icons one by one)

Step 1: check how many hardpoints a craft has (if it is greyed out or has a button to split it from left and right, make sure to have them all visible)

Step 2: Find the Aircraft folder in the mod folder (The name of the aircraft folder is different to the actual name, you can the in game editor to find out which aircraft is which, by selecting them ingame when they are spawned and looking at the name), In this case we are using the Vagrant, so its file is the VTOLTrainer1 file.

Step 3: open the folder. You will as many folders with the name "weaponstation[number]" as there are hardpoints starting at 0[zero).
For example there are 6 on the Vagrant, so the folders will be; weaponstation0, weaponstation1, weaponstation2...

(weaponstation[number] refers to the hardpoints from left to right on the craft, so the first folder [weaponstation0] refers to the "Center Pylon hardpoint
and weaponstation1 refers to the "Left Fuselage Pylon"...

Step 4: Go into one of the "weaponstation[number]" folder's and add the corresponding json file.
example: Center Pylon hardpoint.json (in the weaponstation0 folder)

Step 5: To find weapon names, or what the weapon is, the mod folder should have a txt file named "hardpointdictionary.log" , or you can go into the editor and find out what weapons you have access to. (to figure out what weapons is what, in the editor (under setting and restrictions you can find the name there)

### Before:
```
{
    "allowedWeapons": [
    "AAM2_double_internal",
    "AAM2_double"
    ]
}
```
### After:
```
{
    "allowedWeapons": [
    "AAM1_double_internal",
    "AAM2_double_internal",
    "AAM2_double"
    ]
}
```
For easier addition, you might want to add the new weapon on the first line or the middle line. Be sure to add a "," to each weapon line you add, Except the last one.

That should be all you need to do to add additional weapons to the craft.
(note, you dont have to add the existing weapons of the craft to the file, they will stay on the craft and the added ones with just be, well, added)

## Features
- **Dynamic JSON Parsing:** Automatically inject custom loadouts natively from the `loadout-preset` directory during runtime.
- **Limitless Customization:** Unlocks previously restricted vehicle platforms and external payloads. Add virtually any weapon or pod to any aircraft by simply adding a line to a config file.
- **Full Support for Custom Airframes:** Flawlessly hooks into modded aircraft
- **Cargo MSV:** Not just HLT, MSV are now transporatble as well.
