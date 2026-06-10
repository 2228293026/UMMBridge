# UMMBridge

Run [UnityModManager](https://github.com/newman55/unity-mod-manager) (UMM) mods under [MelonLoader](https://github.com/LavaGang/MelonLoader).

## How it works

Uses Harmony to intercept the `UnityModManager.modsPath` getter, redirecting it to `UMMMods/` directory, then boots UMM. Traditional UMM mods and MelonLoader mods coexist seamlessly.

## Usage

1. Drop the compiled DLL into ADOFAI's `Plugins/` directory
2. Create `UMMMods/` in the game root directory
3. Place UMM mods into `UMMMods/`

## Build

Open `UMMBridge.slnx` in Visual Studio 2022. The `Deploy|x64` configuration outputs directly to the game's Plugins directory.
