# Realistic Aviation

A Cities: Skylines II code mod that makes airports behave more like real ones.

> Vibe-coded with Claude (Anthropic). Reviewed and play-tested by the author.

## Phase 1: tower control (this version)

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
- **Line up and wait (per airport).** A checkbox in each airport's info panel. The same panel lists every runway end in use
  (game runways are usually one-way) with a right-hand traffic pattern checkbox, ticked by default when a parallel
  runway lies to its left so that the pattern stays on the outside. Parallel runways are named L/R (or L/C/R), e.g. RWY 07L. When on, the next departure may enter
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
- **Airport closure (phase 4).** While an active storm, hail or tornado comes within its radius plus 1 km of a runway,
  or the airport building is in danger, the airport is closed: no clearances, departures hold short, gate airplanes
  are not cleared to push back, arrivals on final go around and then hold, airborne ones hold, and landed airplanes
  taxi in as usual. The airport info panel shows "Airport closed" with the event's name. Which disasters close
  an airport (tornado, hail, other) and a maximum closure time are options.
- **Tower radio (phase 4-2).** The airplane info panel shows the tower's latest instruction and the pilot's readback in
  standard English phraseology, e.g. "ATC: Line102, runway 07, cleared for takeoff." The callsign is the route name plus
  the airplane's number on that route (assigned by the mod). The airport info panel shows the last few exchanges.
- **Ground delay (per airport).** In the airport info panel: when as many airplanes move on the ground as the limit
  (default 1.5 per gate), new arrivals hold and departures go first, until two fewer remain.
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

Helicopters are not affected. Only the per-airport line up and wait choice is saved with the city (and dropped
harmlessly if the mod is removed); the tower rebuilds everything else after a save is loaded.

### Options

| Option | Default | Meaning |
| --- | --- | --- |
| Tower contact distance | 5.5 NM | Path distance before the runway at which an arrival asks to land |
| Holding leg length | 1.5 NM | Straight-leg length of the holding pattern |
| Arrival spacing | 45 s | Minimum time between two arrivals cleared to the same runway |
| Weather go-arounds | On | In rain or snow an arrival sometimes goes around at 100 ft (0.5–5% by intensity) |
| Close for tornadoes / hail storms / other disasters | On | Which disasters close an airport |
| Limit closure time | Off | Off: closed as long as the disaster lasts. On: reopens after the maximum closure |
| Maximum closure | 30 min | Game minutes after which a closed airport reopens (when the limit is on) |
| Hold short setback | 30 m | Gap between a waiting airplane's nose and the runway |
| Pushback | On | Push airplanes back out of nose-in gates instead of turning them on the spot |
| Pattern width | 1.5 NM | Runway centerline to downwind |
| Final length | 2 NM | Distance of the base turn before the runway |
| Taxi spacing | 40 m | Gap a taxiing airplane keeps behind the airplane ahead |
| Departure gap before arrivals | 30 s | A ground airplane may go ahead of a cleared arrival at least this far out |

## How it works

`AtcSystem` runs right after the game's `AircraftNavigationSystem` (every 16 frames) as a worker-thread job.
It only lowers the speed (hold short) or replaces the steering target (holding) that the game just computed,
before `AircraftMoveSystem` applies them. `AtcPreNavigationSystem` keeps the game from advancing a holding
airplane along its planned path while it circles.

## Building

Requires the .NET SDK and a local install of the game. Set the game's `Cities2_Data\Managed` path in
`RealisticAviation.csproj.user` (or pass `-p:CS2ManagedPath=...`), then:

```
dotnet build -c Release
```

The DLL is copied to the local `Mods\RealisticAviation` folder.

## License

MIT
