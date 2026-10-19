# Realistic Aviation

A Cities: Skylines II code mod that makes airports behave more like real ones.

> Vibe-coded with Claude (Anthropic). Reviewed and play-tested by the author.

## Features

- **One airplane per runway.** A runway is every runway segment connected through shared nodes, so crossing runways
  count as one. Only the airplane the tower cleared may use it.
- **Hold short.** An airplane on the ground that needs to enter or cross a busy runway stops on the taxiway,
  a configurable distance before the runway edge.
- **Holding.** An arriving airplane that calls the tower while the runway is busy flies a standard right-turn
  racetrack holding pattern at its present position on the arrival path, stacked 1,000 ft above any airplane already
  holding for that runway. When cleared it rejoins its path and the game's own approach takes over.
- **Highest Response Ratio Next.** Arrivals ask the tower when they are within the contact distance; ground airplanes
  ask when they approach the runway. When the runway is free, the request with the highest
  (time waited + expected runway time) / expected runway time goes next: quick users of the runway are favored,
  but every waiting airplane's ratio keeps rising, so nobody waits forever. On a taxiway, the airplane at the head of
  the queue is the one cleared.
- **Line up and wait (per airport).** A checkbox in each airport's info panel: on by default at class B and C airports,
  off at class D, and back to the new class's default when the class changes. The same panel lists every runway end in use
  (game runways are usually one-way) with a right-hand traffic pattern checkbox, ticked by default when a parallel
  runway lies to its left so that the pattern stays on the outside. Parallel runways are named L/R (or L/C/R), e.g. RWY 07L (ICAO style, two digits; four or five parallels as 32L, 32R, 33L, 33R). When on, the next departure may enter
  the runway and wait lined up as soon as the landing airplane has touched down, or the departing airplane has started
  its takeoff roll, and passed its entry point; it takes off the moment the runway is clear. When off, it holds short until then.
- **Arrival sequencing (phase 4).** Several arrivals may be cleared to land one behind the other, the arrival spacing
  apart (plus 20 s per size class when a larger airplane is ahead of a smaller one, for wake turbulence). The runway
  itself is checked at the decision height, 100 ft above it: if it is not clear (an airplane on it, or a departure
  that has not lifted off), the arrival goes around and flies the pattern. A ground airplane cleared onto the runway
  but not yet on it is told to hold short instead.
- **Departures ahead of arrivals.** A ground airplane may take off or cross when the next cleared arrival is still
  far enough out (the departure gap). When ground traffic has waited longer (by response ratio), no new arrival is
  added to the sequence until it has gone.
- **Airport closure (phase 4).** While an active storm, hail or tornado comes within its radius plus 1 NM (1,852 m) of a runway,
  or the airport building is in danger, the airport is closed: no clearances, departures hold short, gate airplanes
  are not cleared to push back, arrivals on final go around and then hold, airborne ones hold, and landed airplanes
  taxi in as usual. The airport info panel shows "Airport closed" with the event's name. Which disasters close
  an airport (tornado, hail, other) and a maximum closure time are options.
- **Tower radio (phase 4-2).** The airplane info panel shows the tower's latest instruction and the pilot's readback in
  standard English phraseology, e.g. "ATC: Line102, runway 07, cleared for takeoff." The callsign is the route name plus
  the airplane's number on that route (assigned by the mod). The airport info panel shows the last few exchanges.
- **Runway numbers** come from each runway's heading on the map, with the game's own north (the north of its sky:
  the sun rises in the east). A north sign for the screen is the separate Map Compass mod; Realistic Aviation does not
  need it.
- **Ground delay (per airport).** Off by default; turn it on in the airport info panel. Then when as many airplanes move on the ground as the limit
  (default 1.5 per gate), new arrivals hold and departures go first, until two fewer remain.
- **Airspace (phase 4-3).** Each airport gets a class from its size (2+ runways or 16+ gates: B, 8+ gates: C, else D;
  radius 5 / 3.5 / 2.5 NM), shown in its panel. Arrivals call when within the radius + 0.5 NM; holds are stacked by
  class (B highest, from 6,000 / 4,000 / 2,000 ft); a class D airport has no approach control (its tower says "continue
  approach"); the default pattern side turns away from a neighbouring airport.
- **VFR chart infoview.** A button in the infoview menu draws the map like a VFR sectional chart:
  - airspace rings (B solid blue, C solid magenta, D dashed blue);
  - their altitude limits in hundreds of feet above sea level ("100 / SFC", "40 / 12", and "25" between corner
    brackets for class D, "-25" under a class B or C), using typical FAA values above the field: B to 10,000 ft
    (shelf from 3,000), C to 4,000 ft (shelf from 1,200), D to 2,500 ft;
  - a Mode C veil around each class B airport: a thin magenta ring "MODE C 6 NM" (the chart's 30 NM scaled to the
    map: the class B radius plus 1 NM, so it crosses the edge of the playable area);
  - built-up areas in yellow, where residents and workers reach 2,500 per km²;
  - terrain in the chart's eight elevation tints, fitted to the map's height (at least 200 ft per tint), with spot
    elevations on peaks and Maximum Elevation Figures (MEF) per quadrant;
  - obstacles: every building at least 200 ft high, with its top above sea level and its height above the ground
    ("1324 (312)"). The highest are shown first; a lower one within 600 m turns that symbol into the group symbol,
    so labels never overlap. Symbols follow the FAA legend:
    - the spire from 1,000 ft, else ^;
    - lightning rays above 700 ft;
    - groups show the elevation only;
    - inside built-up areas only obstacles over 299 ft are shown.

    Wind turbines have their own symbol. Two to four within 600 m of each other are a turbine group; five or more are
    a wind farm, drawn with a dashed outline round them and its highest elevation in a box;
  - the airport buildings in their airspace colour: blue for class B and D, magenta for class C.

  Like a printed chart, it is worked out once when the infoview opens; reopen it to see changes. Each layer can be turned
  off in the legend.
- **ICAO code.** Each airport's panel has a code field (3–4 letters or digits). An empty field means an automatic code
  from the airport's name. The code is saved with the city and used by Aviation Weather Add-on.
- **Gate hold.** An airplane taxiing in to a gate that another airplane still uses waits 100 m before the stand,
  clear of the pushback area ("hold position, gate occupied").
- **Pushback (phase 2).** An airplane leaving a nose-in gate is pushed back a short way (about half its length,
  15–40 m) at 3 m/s with its nose locked, then swung around at 15°/s until it faces along its route, and taxis off
  after a 10 s engine start. Ground control approves the pushback only when no airplane is moving close to the area
  (at most 60 s wait); taxiing airplanes stop short of an airplane being pushed back.

- **Traffic pattern (phase 3).** After a go-around the airplane climbs straight ahead and flies a left-hand pattern (right-hand
  where set for that runway end in the airport info panel, e.g. "RWY 27 right-hand pattern") built from its own runway: upwind past the runway end, crosswind, downwind 1,500 ft above the
  runway, then base and final once cleared (extending downwind for up to 2 NM if not, then holding). A parallel runway on the pattern
  side moves the pattern to the other side. Airliners normally fly straight-in approaches, so arrivals do not fly the
  pattern otherwise.

Helicopters are not affected. The per-airport choices (line up and wait, pattern side per runway end, airspace class,
ground delay) are saved with the city and dropped harmlessly if the mod is removed; the tower rebuilds everything else
after a save is loaded.

### Options

| Option | Default | Meaning |
| --- | --- | --- |
| Holding leg length | 1.5 NM | Straight-leg length of the holding pattern |
| Arrival spacing | 45 s (15–75) | Minimum time between two arrivals cleared to the same runway |
| Weather go-arounds | On | An arrival sometimes goes around at 100 ft: 0.3% in clear weather, rising with the intensity to 8% (rain), 12% (snow, fog), 15% (thunderstorm) |
| Close for tornadoes / hail storms / other disasters | On | Which disasters close an airport |
| Limit closure time | Off | Off: closed as long as the disaster lasts. On: reopens after the maximum closure |
| Maximum closure | 30 min | Game minutes after which a closed airport reopens (when the limit is on) |
| Hold short setback | 30 m | Gap between a waiting airplane's nose and the runway |
| Pushback | On | Push airplanes back out of nose-in gates instead of turning them on the spot |
| Pattern width | 1.5 NM | Runway centerline to downwind |
| Final length | 2 NM | Distance of the base turn before the runway |
| Taxi spacing | 40 m | Gap a taxiing airplane keeps behind the airplane ahead |
| Departure gap before arrivals | 30 s (10–50) | A ground airplane may go ahead of a cleared arrival at least this far out |

## Compatibility

- Built and tested on game version **1.6.2f1**.
- Played together with **Traffic** (krzychu124) and **C2VM Traffic Tool Essentials**.
- It changes only how airplanes are steered (speed, steering target, facing during pushback); runways, taxiways and
  routes are left as the game builds them. Mods that also take over airplane movement may conflict.
- In-game text is available in English and Korean; radio calls are in English (ICAO/FAA phraseology).

## How it works

`AtcSystem` runs right after the game's `AircraftNavigationSystem` (every 16 frames) as a worker-thread job.
It only lowers the speed (hold short) or replaces the steering target (holding) that the game just computed,
before `AircraftMoveSystem` applies them. `AtcPreNavigationSystem` keeps the game from advancing a holding
airplane along its planned path while it circles.

## Building from source

Requirements: the game and the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer. The official modding toolchain is not needed, because the mod has no Burst code.

```
dotnet build -c Release -p:CS2ManagedPath="<game folder>\Cities2_Data\Managed"
```

Instead of passing the path every time, you can create `RealisticAviation.csproj.user` next to the project file (it is git-ignored):

```xml
<Project>
  <PropertyGroup>
    <CS2ManagedPath>D:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Managed</CS2ManagedPath>
  </PropertyGroup>
</Project>
```

Without either, the build looks in the default Steam location. The output (`RealisticAviation.dll`, `RealisticAviation.mjs`) is copied to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\RealisticAviation`, where the game loads it as a local mod. Remove that folder before subscribing to the published version, otherwise the mod loads twice.

## License

[MIT](LICENSE) © Boing787(kanghyeon01)
