# Writing an add-on for Realistic Aviation

Realistic Aviation (RA) offers one public entry point for other mods: the static class `RealisticAviation.Api`, split by
area. Use only `Api` and the types it hands out (`RadioMessage`, `AirportInfo`, `RunwayEndInfo`, `FlightInfo`,
`FlightState`, `OperatingOverrides`, `OperatingValues`, `AtisState`, `AirspaceClass`, `AtcMessageKind`, `AtcFrequency`).
Every other public type in `RealisticAviation.dll` exists for the game (systems, info panel sections, save components) and
may change in any version.

Aviation Weather Add-on ([source](https://github.com/kanghyeon01/AviationWeatherAddon)) is a complete example. It reads
the airports through `Api` and gives the tower each airport's ATIS.

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

3. **Check the version** when your mod needs something added after version 1:

   ```csharp
   if (RealisticAviation.Api.Version < 2) { /* tell the player to update Realistic Aviation */ }
   ```

   `Api.Version` is a property. Read it at run time, as above; never copy it into a constant.

## Rules

- **Main thread only.** Call `Api` from a system's `OnUpdate`, `OnCreate` or a UI callback, never from a job.
- **No city, no answers.** Before a city is loaded, `Api.IsReady` is false and the members return nothing.
- **Orders and overrides take one tower update.** The tower runs every 16 simulation frames. What you set or order
  reaches it at its next update.
- **Saved or not.** The airport settings the info panel also changes are saved with the city. These are the airspace
  class, the code, line up and wait, the ground delay and the pattern side. Everything else is not saved: closures,
  operating overrides, holds, priorities and providers. Set them again when a city is loaded, for example in
  `OnGameLoadingComplete`.
- **Events come once per tower update**, in order, on the main thread.
  - Keep handlers short; they run inside the tower's update.
  - A handler that throws is logged to `RealisticAviation.log` and skipped. The tower and other add-ons go on.
- **Unsubscribe and unregister** in your system's `OnDestroy`.
- **Logging**: write per-airplane lines only when `Api.Options.DetailedLog` is on (the player's "Detailed log" option).

## What `Api` offers (version 5)

### `Api`

| Member | What it does |
|---|---|
| `Version` | Interface version (2 from Realistic Aviation 1.2.2, 3 from 1.2.3, 4 from 1.2.4, 5 from the IFR chart update) |
| `IsReady` | A city is loaded and the tower is running |

### `Api.Airports`

| Member | What it does |
|---|---|
| `GetAll(List<Entity>)` | Every airport: top-level buildings with airplane stops, outside connections excluded |
| `Find(Entity)` | The airport an entity belongs to (the airport itself, a runway, a gate...), or `Entity.Null`. Only its structures count (the main building, roads and runways, stops); its upgrade buildings, vehicles and people belong to none (from 1.2.4) |
| `TryGetInfo(airport, out AirportInfo)` | Code, class, centre, radius, hold base, runways, gates, closure and its reason, ground delay, line up and wait |
| `TryGetAirspaceClass(airport, out AirspaceClass)` | Class B, C or D |
| `SetAirspaceClass(airport, cls)` / `SetAutomaticAirspaceClass(airport)` | Choose the class, or follow the size again (saved; applied next frame) |
| `GetCode(airport)` / `SetCode(airport, code)` | The ICAO code. Set 3–4 letters or digits; null brings back the automatic code (saved) |
| `SetLineUpAndWait(airport, bool)` | Line up and wait (saved) |
| `GetGroundDelay(airport, out limit)` / `SetGroundDelay(airport, enabled, limit)` | Ground delay (saved) |
| `Close(airport, reason)` / `Open(airport)` | Close until opened again. The reason shows in the panel and on the radio. Not saved |
| `IsClosed(airport, out closedBy)` | Closed, and by what (a disaster event, or the airport or runway an add-on closed) |
| `GetLandingRunways(airport, List<string>)` | Runway ends in use for landing, named as on the radio, sorted |
| `SetOverrides(airport, OperatingOverrides)` / `ClearOverrides(airport)` / `TryGetOverrides` | Per-airport operating values over the options. Null fields keep the options. Not saved |
| `GetOperatingValues(airport)` | The values in use now: options, weather and overrides |

`OperatingOverrides` fields:

- `ArrivalSpacingSeconds`
- `DepartureGapSeconds`
- `GlidePath` (fly the instrument approach procedure at this airport), `GlidePathDegrees`
- `FinalLengthMeters`
- `PatternWidthMeters`
- `WeatherGoAroundChance` (0–1; replaces the weather's chance)

Added in version 2 (Realistic Aviation 1.2.2), so that every option can be set per airport:

- `InstrumentApproach` (the same as `GlidePath`, which counts when this is null), `StandardArrival`, `StandardDeparture`
- `ApproachHeightLimits`
- `WeatherGoArounds` (off makes the chance 0, on uses the climate's; `WeatherGoAroundChance` replaces either)
- `HoldingLegLengthMeters`, `HoldShortSetbackMeters`, `TaxiSpacingMeters` (0 turns the taxi spacing off)
- `Pushback`, `PushbackDistanceMeters` (fixed distance, 5–100 m; null keeps half the airplane's length, 15–40 m)
- `CloseForTornado`, `CloseForHail`, `CloseForOtherDisasters`, `LimitClosureTime`, `MaxClosureMinutes`

Added in version 4 (Realistic Aviation 1.2.4):

- `DepartureAheadMeters`, `DepartureAheadShare`: an arrival crosses the threshold behind a departure that has lifted off
  only once that departure is min(`DepartureAheadMeters`, `DepartureAheadShare` × runway length) past the threshold, or
  past the runway end (defaults 1,829 m = FAA 6,000 ft and 0.6; share clamped to 0–1)

Added in version 5 (next update):

- `InitialMocFeet` (default 1,000), `IntermediateMocFeet` (500): minimum obstacle clearance of the initial segments and
  STAR holds, and of the intermediate segment. The tower raises the IF, the IAFs and the STAR hold bases until the obstacles
  under them have this much (the FAF stays on the glide path)
- `ObstacleAreaPrimaryMeters`, `ObstacleAreaSecondaryMeters` (1,000 each): width of the obstacle areas each side of a
  segment; full MOC in the primary area, tapering to 0 across the secondary
- `DecisionHeightFeet` (default 200, CAT I; was 100 ft before this update): where an arrival decides to land or go around
  (runway check, weather go-around). Clamped to 50–1,000 ft

`OperatingValues` has the same members (`PushbackDistanceMeters` 0 = the default length rule, `MaxClosureMinutes` 0 = no
limit). Not per airport: "Detailed log" and the "Remove this airplane" button (read them from `Api.Options`).

### `Api.Runways`

| Member | What it does |
|---|---|
| `GetEnds(airport, List<RunwayEndInfo>)` | Runway ends in use (every airport for `Entity.Null`): name ("27L"), start, direction, length, pattern side, closed |
| `Close(runway, reason)` / `Open(runway)` | Close one runway (`RunwayEndInfo.Runway`). Not saved |
| `SetRightHandPattern(runway, direction, bool)` | Traffic pattern side for the end landing in that direction (saved) |

### `Api.Flights`

| Member | What it does |
|---|---|
| `GetAll(airport, List<FlightInfo>)` / `TryGet(airplane, out FlightInfo)` | What the tower is doing with each airplane |
| `GoAround(airplane, reason)` | Send an arrival on final around. Other airplanes ignore the order |
| `HoldClearance(airplane, bool)` | Hold back its next clearance: an arrival keeps holding, a departure keeps holding short. A clearance it already has is kept |
| `SetPriority(airplane, bool)` | Ahead of every other airplane waiting for its runway (an emergency, say) |

`FlightInfo` fields:

- `Airplane`, `Airport`, `Runway`
- `IsArrival`, `IsCrossing`
- `State`: `Waiting`, `Holding`, `InPattern`, `LineUp` or `Cleared`
- `WaitedSeconds`, `EtaSeconds`
- `HoldAltitudeMeters`
- `IsHeld`, `HasPriority`

### `Api.Radio`

| Member | What it does |
|---|---|
| `GetLog(airport, frequency, List<RadioMessage>, max)` | Latest transmissions on a frequency (broadcasts on all), newest first |
| `TryGetLastMessage(airplane, out RadioMessage)` | The airplane's latest transmission |
| `GetText(message, out first, out second, out pilotFirst)` | The lines as the panels show them ("ATC: ..." and "callsign: ...") |
| `Post(airport, frequency, airplane, callsign, controller, pilot, pilotFirst)` | Add your own transmission. It shows in the panels like the tower's. With an airplane (any entity, a helicopter too) it is on that frequency only; without one it is a broadcast |
| `AreaCode` | The city's telephone area code in the tower's phone number (pilot deviation calls; the number is always <area code>-555-01XX, a range set aside for fiction). Reads the one in use. Set 3 digits (NXX: 2–9, 0–8, 0–9, not N11), or null for the one made up from the city's name. Version 3 |
| `SetGoAroundReasonWeight(GoAroundReason, WeatherKind, weight)` / `GetGoAroundReasonWeight(...)` | How often a pilot's own go-around gives that reason in that weather ("going around, windshear"), relative to the other reasons in it; city-wide. 0 never gives it; all 0 = no reason. Not saved. Reasons: `UnstableApproach`, `RunwayNotInSight`, `Windshear`, `GustyWinds`, `WakeTurbulence`, `BirdsOnRunway` (new ones come at the end); weather: `Clear`, `Rain`, `Snow`, `Fog`, `Thunderstorm`. Version 4 |
| `RegisterCallsignProvider` / `Unregister...` | Supply callsigns (airline codes, say). The latest registered is asked; return null to keep RA's |
| `RegisterPhraseProvider` / `Unregister...` | Change the words of every transmission (another language or phraseology). All registered are asked, in order. Your own posts are not changed |

### `Api.Events`

| Event | When |
|---|---|
| `MessageSent(RadioMessage)` | Every transmission, posts included |
| `ApproachCleared`, `LandingCleared` | An arrival is cleared for the approach, or to land |
| `GoAround` | An arrival goes around (told, or on its own) |
| `HoldingStarted` | An airplane is told to hold (a descent in the holding stack, 1.2, is a `MessageSent` with `AtcMessageKind.DescendInHold`; its altitude is in the text; version 3 adds `PilotDeviation`, also only a `MessageSent`) |
| `Landed` | An arrival has landed (handed to ground) |
| `PushbackApproved`, `LineUpCleared`, `TakeoffCleared` | A departure is approved or cleared (`RadioMessage.NoDelay`: "cleared for takeoff, no delay", an arrival close behind) |
| `TookOff` | A departure has taken off (handed to departure control) |
| `AirportClosed(airport, cause)`, `AirportOpened(airport)` | Closures |
| `GroundDelayStarted(airport)`, `GroundDelayEnded(airport)` | Ground delays |
| `AirspaceClassChanged(airport, cls)` | A class changed (size, panel or add-on), or a new airport got one |
| `RunwaysChanged()` | Runways were built, changed or removed |

### `Api.Atis`

| Member | What it does |
|---|---|
| `RegisterProvider(AtisProvider)` / `UnregisterProvider` | Give the tower each airport's ATIS (`AtisState`), for "with information X". The latest registered is used |

### `Api.Options` (read only)

`DetailedLog`, `InstrumentApproach`, `StandardArrival`, `StandardDeparture`, `GlidePathDegrees`, `ApproachHeightLimits`, `ArrivalSpacingSeconds`,
`DepartureGapSeconds`, `FinalLengthNm`, `PatternWidthNm`, `HoldingLegNm`, `WeatherGoArounds`, `Pushback`, `TaxiSpacingMeters`,
`HoldShortSetbackMeters`. `GlidePath` (= `InstrumentApproach`) and `SmoothDepartureTurns` (= `StandardDeparture`) are kept for add-ons built for 1.1.

## Example: an emergency add-on

Gives priority to every arrival whose radio text says "EMERGENCY" once it is told to hold, and logs go-arounds.

```csharp
using Game;
using RealisticAviation;
using Unity.Entities;

namespace MyAddon // your mod's namespace, so that Mod below is your own Mod class
{
    public partial class EmergencySystem : GameSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            Api.Events.HoldingStarted += OnHolding;
            Api.Events.GoAround += OnGoAround;
        }

        protected override void OnDestroy()
        {
            Api.Events.HoldingStarted -= OnHolding;
            Api.Events.GoAround -= OnGoAround;
            base.OnDestroy();
        }

        private void OnHolding(RadioMessage message)
        {
            Api.Radio.GetText(message, out string first, out string second, out _);
            if ((first + second).Contains("EMERGENCY"))
            {
                Api.Flights.SetPriority(message.Airplane, true);
            }
        }

        private void OnGoAround(RadioMessage message)
        {
            Api.Radio.GetText(message, out string first, out string second, out _);
            Mod.Log.Info($"Go-around at {Api.Airports.GetCode(message.Airport)}: {first} / {second}");
        }

        protected override void OnUpdate() { }
    }
}
```

Register the system in your mod's `OnLoad`, as for any system, for example
`updateSystem.UpdateAt<EmergencySystem>(SystemUpdatePhase.GameSimulation);`.

## Not offered

- Drawing the airplanes' paths yourself, or replacing the tower's decisions wholesale. The tower runs on a worker thread
  and keeps every airplane it controls (holding, in the pattern, held short) on its own targets, every update. Use the
  orders and overrides above instead.
- Writing RA's save components (`LineUpSetting`, `AirportCode` and the others) directly. Use the setters above, which
  keep them consistent.

## Compatibility promise

From version 1 on, `Api` only grows: new members and types, new fields at the end of the info structs, new enum values
at the end. Nothing is renamed, removed or changed in shape, so an add-on built against an older version keeps working.
Raise your check of `Api.Version` when you use something new.
