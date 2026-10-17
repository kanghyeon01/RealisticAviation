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
- **Line up and wait (per airport).** A checkbox in each airport's info panel. When on, the next departure may enter
  the runway and wait lined up as soon as the landing airplane has touched down, or the departing airplane has started
  its takeoff roll, and passed its entry point; it takes off the moment the runway is clear. When off, it holds short until then.
- **Departures ahead of arrivals.** A ground airplane may take off or cross ahead of a cleared arrival that is still
  far enough out (the departure gap).
- **Pushback (phase 2).** An airplane leaving a nose-in gate is pushed back a short way (about half its length,
  15–40 m) at 3 m/s with its nose locked, then swung around at 15°/s until it faces along its route, and taxis off
  after a 10 s engine start. Ground control approves the pushback only when no airplane is moving close to the area
  (at most 60 s wait); taxiing airplanes stop short of an airplane being pushed back.

Helicopters are not affected. Only the per-airport line up and wait choice is saved with the city (and dropped
harmlessly if the mod is removed); the tower rebuilds everything else after a save is loaded.

### Options

| Option | Default | Meaning |
| --- | --- | --- |
| Tower contact distance | 5.5 NM | Path distance before the runway at which an arrival asks to land |
| Holding leg length | 1.5 NM | Straight-leg length of the holding pattern |
| Hold short setback | 30 m | Gap between a waiting airplane's nose and the runway |
| Pushback | On | Push airplanes back out of nose-in gates instead of turning them on the spot |
| Taxi spacing | 40 m | Gap a taxiing airplane keeps behind the airplane ahead |
| Departure gap before arrivals | 90 s | A ground airplane may go ahead of a cleared arrival at least this far out |

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
