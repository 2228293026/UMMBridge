# UMMBridge

Run [UnityModManager](https://github.com/newman55/unity-mod-manager) (UMM) mods under [MelonLoader](https://github.com/LavaGang/MelonLoader).

## How it works

Uses Harmony to intercept the `UnityModManager.modsPath` getter, redirecting it to `UMMMods/` directory, then boots UMM. Traditional UMM mods and MelonLoader mods coexist seamlessly. A button to open the mods folder is added at the top-right of the UMM window.

## Usage

1. Drop the compiled DLL into ADOFAI's `Plugins/` directory
2. Place UMM mods into `UMMMods/` (created automatically on first launch)
3. Open the UMM window and click **Mods Dir** at the top-right

## Build

```
dotnet build UMMBridge\UMMBridge.csproj -c Release
```

Or open `UMMBridge.slnx` in Visual Studio 2022. The `Deploy` configuration outputs directly to the game's Plugins directory.


<img width="1282" height="752" alt="image" src="https://github.com/user-attachments/assets/04447190-7fb8-4254-8091-63512e2deda0" />
