# Realistic Aviation – plan and design notes

Status as of 2026-10-17. Game version 1.6.2f1. Author: Boing787 (kanghyeon01).

## Phases

| # | Phase | Status |
| --- | --- | --- |
| 1 | Tower control (ATC) | **Done, tested in game** |
| 2 | Pushback | Implemented, testing in game |
| 3 | Traffic pattern (upwind, crosswind, downwind, base, final) | Planned |
| 4 | Go-around, together with arrival sequencing | Planned |
| 5 | Alternate airport (optional, toggle) | Idea |
| – | Snow-free runways | **Dropped** (snow is a GPU snow-map shader effect; too hard) |

Possible extra: an "ATC" section in the vehicle info panel showing each airplane's tower status, plus phraseology
templates. No LLM is needed for this.

## Phase 1 – tower control (done)

- **Runway** = runway edges connected through shared nodes, plus those nodes. Crossing runways count as one.
  Union-find over edges and nodes; the key is the lowest-index root.
- **Airport** = top-level building owning AirplaneStops, outside connections excluded. Runway → airport is the owner
  if the runway is part of the airport, otherwise the nearest airport.
- **Requests.** Arrivals ask at the contact distance (option, NM). Ground airplanes ask about 200 m (plus their
  stopping distance) before the runway.
- **Scheduling.** HRN: ratio = (wait + expected runway time) / expected runway time. Arrival service time is ETA + 30 s.
  For ground airplanes the head of the taxiway queue (closest to the runway) is cleared, but the queue's wait counts
  from its oldest request.
- **Departure ahead of arrival.** A departure may go when the cleared arrival's ETA is above the departure gap
  (option, 90 s).
- **Hold short.** Lowers `AircraftNavigation.m_MaxSpeed` so the airplane brakes to a stop before the runway.
- **Holding at present position.** Right-turn racetrack with the fix where the hold starts, inbound along the path,
  stacked 1,000 ft. The game's nav target and direction are saved. `AtcPreNavigationSystem` pushes the target 1000 km
  away before navigation, so the game does not advance airway lanes. On release, the airplane flies back to the saved
  point, lines up through a gate behind it, then gets the game's own target back.
  Never touch lane flags or lane buffers: that caused speed jumps, a 190 m/s touchdown and pitch bobbing.
- **Line up and wait (per airport).** A checkbox in the airport info panel (`LineUpSection` + `UI/RealisticAviation.mjs`)
  sets an `IEmptySerializable` tag on the airport building.
  - A departure may line up behind a landing airplane that has touched down, or behind a departure on its takeoff roll
    when no arrival is cleared. The traffic must be more than 150 m past the entry point along the runway direction.
  - The runway direction is taken from the first `CarLaneFlags.Runway` lane, not the junction lane.
  - The airplane stops with its tail 10 m inside the runway lane and is cleared for takeoff when the runway is clear.
- **Taxi spacing.** Keeps a gap (option, 40 m) behind the airplane ahead, using the lane objects along the path.
- **Safety nets.** Clearance timeouts: ground 3 game minutes, air 5. The runway map is rebuilt only when the entity
  signature of taxiway edges and airplane stops changes.

## Phase 2 – pushback (next)

Implemented 2026-10-17: `UpdatePushback` in AtcSystem. Trigger: ground airplane below 1 m/s with route lanes and its nose
against its path (cos < -0.5); gate airplanes keep `Moving` while boarding, so "just started moving" does not work.
It is pushed at 3 m/s with `m_TargetDirection` locked for half its length (15–40 m; the author asked for a short push
after long and diagonal pushes looked wrong), then swung in place at 15°/s and released. While the path is unknown
it waits with the nose locked. Taxiing airplanes stop short of a `PushbackArea` (a capsule over the rest of the push).
Phases: AwaitingApproval (no airplane moving within 40 m of the push area or standing in it; 60 s timeout) → Pushing
→ Turning → EngineStart (10 s; the author asked for less than 20 s) → released. Option "Pushback" (on by default).
Tug model skipped: there is no vanilla tug asset and a separate vehicle would be costly.
Original note: at the gate the game turns the airplane 180° instantly. Idea: on the ground `AircraftMoveSystem` faces
`m_TargetDirection` when it is set. Keep the nose locked towards the gate while the airplane moves backwards at low
speed, swing the tail, then release it. A tug model is a later extra. First study the gate and parking-lane geometry.

## Phase 4 – go-around + arrival sequencing (planned)

Today one cleared arrival owns the runway from about 5 NM until it vacates, so a runway handles about one arrival every
1.5–2 game minutes. Plan:
- Arrivals follow each other in trail by a time gap; runway exclusivity applies only from touchdown.
- Go-around when the runway is not clear at short final. This includes a departure ahead of the arrival that has not
  rotated yet.
- Weather triggers (`ClimateSystem` precipitation, fog, thunder; `WindSystem.GetWind` for crosswind).

## Key game facts (from decompiled Game.dll)

- `AircraftNavigationSystem` and `AircraftMoveSystem` both run every 16 frames, offset 10, on all aircraft;
  timestep is 4/15 s.
- Airborne airplanes ignore traffic. On the ground, airplanes use the car lane reservation and overlap system with
  constant priority 104.
- The move system sets velocity to exactly `m_MaxSpeed`, so any speed you set must stay within
  `VehicleUtils.CalculateSpeedRange`.
- Approach sequence: airway lane → `Approaching` (aligned with the runway) → runway lane with `Approaching|Landing`
  → touchdown clears `Flying`.
- UI: the game bundle is `Cities2_Data\Content\Game\UI\index.js`. Info sections are keyed by C# type full name in
  `selectedInfoSectionComponents`.

## Coding rules (from the author)

- Always do an optimization pass. The tower runs as a worker-thread IJob without Burst.
- English-only code and comments. Ship both EN and KO locales.
- Diagnostics: tower events are logged to `Logs/RealisticAviation.log`.

## Phase 5 – alternate airport (optional, idea)

Proposed by the author on 2026-10-17. An option that can be turned on or off: after N go-arounds (an option) the
airplane diverts. Mechanism (not verified yet): set a new `Target` and mark the path obsolete so the game repaths.
The alternate is usually an outside air connection (a diversion off the map), or another airport in the city if
there is one. Open question to study first: what the game does with passengers on board and with the line/route
state of a public-transport airplane whose target changes.
