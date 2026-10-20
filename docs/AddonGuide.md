# Writing an add-on for Realistic Aviation

Realistic Aviation (RA) offers one public entry point for other mods: the static class `RealisticAviation.Api`.
Use only `Api` and the types it hands out. Every other public type in `RealisticAviation.dll` exists for the game
(systems, info panel sections, save components) and may change in any version.

Aviation Weather Add-on ([source](https://github.com/kanghyeon01/AviationWeatherAddon)) is a complete example. It
reads the airports through `Api` and gives the tower each airport's ATIS.

## Setup

1. **Reference RA without shipping it.** In your `.csproj`:

   ```xml
   <ItemGroup>
     <Reference Include="RealisticAviation">
       <HintPath>lib\RealisticAviation.dll</HintPath>
       <Private>false</Private>
     </Reference>
   </ItemGroup>
   ```

   - Take `RealisticAviation.dll` from the subscribed copy, under
     `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\.cache\Mods\pdx_mods\164022_*\`, and put it in
     `lib\`.
   - `<Private>false</Private>` keeps it out of your mod's folder. The game loads the one from the subscription.

2. **Declare the dependency.** In `Properties/PublishConfiguration.xml`, so Paradox Mods subscribes RA with your mod:

   ```xml
   <Dependency Id="164022" />
   ```

3. **Check the version.** If you need something added after version 1, check it when your mod loads:

   ```csharp
   if (RealisticAviation.Api.Version < 2) { /* tell the player to update Realistic Aviation */ }
   ```

   `Api.Version` is a property. Read it at run time, as above; never copy it into a constant.

## Rules

- **Main thread only.** Call `Api` from a system's `OnUpdate`, `OnCreate` or a UI callback, never from a job.
- **No city, no answers.** Before a city is loaded, `Api.IsReady` is false and the methods return nothing.
- **Events come once per tower update** (every 16 simulation frames), in order, on the main thread.
  - Keep handlers short; they run inside the tower's update.
  - A handler that throws is logged to `RealisticAviation.log` and skipped. The tower and other add-ons go on.
- **Unsubscribe and unregister** in your system's `OnDestroy`.
- **Logging**: write per-airplane lines only when `Api.DetailedLog` is on (the player's "Detailed log" option).

## What `Api` offers (version 1)

| Member | What it gives |
|---|---|
| `Version`, `IsReady`, `DetailedLog` | Interface version; whether a city is loaded; the "Detailed log" option |
| `GetAirports(List<Entity>)` | Every airport in the city (top-level buildings with airplane stops, outside connections excluded) |
| `GetAirportOf(Entity)` | The airport an entity belongs to (the airport itself, a runway, a gate...), or `Entity.Null` |
| `TryGetAirspaceClass(airport, out AirspaceClass)` | Class B, C or D |
| `GetLandingRunways(airport, List<string>)` | Runway ends in use for landing, named as on the radio ("27", "09L"), sorted |
| `IsClosed(airport, out Entity closedBy)` | Whether the tower closed the airport, and what closed it |
| `GetAirportCode(airport)` | The ICAO code: entered by the player, or made from the name |
| `TryGetLastMessage(airplane, out RadioMessage)` | The airplane's latest transmission |
| `GetRadioText(message, out controller, out pilot, out pilotFirst)` | The words, as the info panels show them |
| `MessageSent` (event) | Every transmission |
| `GoAround` (event) | An airplane goes around, told by the tower or on its own |
| `AirportClosed` / `AirportOpened` (events) | An airport closes (with what closed it) or opens again |
| `RegisterAtisProvider` / `UnregisterAtisProvider` | Give the tower each airport's ATIS, for "with information X". With several providers, the latest registered one is used |

`RadioMessage` holds `Kind` (`AtcMessageKind`), `Frequency` (`AtcFrequency`), `Airplane` (`Entity.Null` for a
broadcast), `Airport`, `Runway`, `Event` and `Frame`.

## Example: a go-around counter

```csharp
using Game;
using RealisticAviation;
using Unity.Entities;

namespace MyAddon // your mod's namespace, so that Mod below is your own Mod class
{
    public partial class GoAroundCounterSystem : GameSystemBase
    {
        private int m_Count;

        protected override void OnCreate()
        {
            base.OnCreate();
            Api.GoAround += OnGoAround;
        }

        protected override void OnDestroy()
        {
            Api.GoAround -= OnGoAround;
            base.OnDestroy();
        }

        private void OnGoAround(RadioMessage message)
        {
            m_Count++;
            Api.GetRadioText(message, out string controller, out string pilot, out bool pilotFirst);
            Mod.Log.Info($"Go-around #{m_Count} at {Api.GetAirportCode(message.Airport)}: {(pilotFirst ? pilot : controller)}");
        }

        protected override void OnUpdate() { }
    }
}
```

Register the system in your mod's `OnLoad`, as for any system, for example
`updateSystem.UpdateAt<GoAroundCounterSystem>(SystemUpdatePhase.GameSimulation);`.

## Do not

- Read or write RA's save components (`LineUpSetting`, `AirportCode` and the others) or its systems' internals. They
  are not part of `Api`, and changing them can break saves.
- Move airplanes that RA controls: holding, flying the pattern, or held short. RA decides their targets every update
  and would undo yours.
