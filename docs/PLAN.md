# Realistic Aviation – plan and design notes

Status as of 2026-10-17. Game version 1.6.2f1. Author: Boing787 (kanghyeon01).

## Phases

| # | Phase | Status |
| --- | --- | --- |
| 1 | Tower control (ATC) | **Done, tested in game** |
| 2 | Pushback | **Done, tested in game** (approval wait not yet seen) |
| 3 | Traffic pattern (upwind, crosswind, downwind, base, final) | **Done, tested in game** |
| 4-1 | Go-around at decision height, arrival sequencing, disaster closure | Implemented 2026-10-17, testing in game |
| 4-2 | ATC radio text (callsign, instruction, readback) in the info panels | Implemented 2026-10-17, testing in game |
| 4-3 | Airspace: control zone per airport, split where zones overlap; no holds in another airport's zone; contact on zone entry | Proposed |
| 4-4 | Departure and climb procedure | **Dropped** (the steep climb is the game's 20 degree climb angle for every airplane; the author prefers not to touch game defaults) |
| 4-5 | Ground flow control: when too many airplanes taxi or wait on the ground at an airport (not counting ones at gates), approach stops clearing new arrivals (they hold, "expect further clearance, ground delay") and departures go first; threshold an option (fixed count, or from the gate count if it can be found) | Implemented 2026-10-18 (per-airport limit in the airport panel, default 1.5 per gate, hysteresis 2), testing in game |
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

## Phase 2 – pushback (done)

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

## Phase 4-1 – go-around + arrival sequencing + closure (implemented 2026-10-17)

Before: one cleared arrival owned the runway from about 5 NM, about one arrival every 1.5–2 game minutes.
- `RunwayState` no longer holds an arrival. Each tick `NoteArrival` rebuilds the arrival picture from the cleared,
  airborne arrivals: the soonest ETA, the latest ETA and its size class, and the arrival rolling out after touchdown
  (for line up and wait). Airborne arrivals on the runway lane do not count as occupants; a departure lifting off does.
- `GrantRunways`: an arrival is a candidate when its ETA is at least the latest cleared ETA + arrival spacing (option,
  45 s; was 75 s) + 20 s per size class step when a larger airplane is ahead (`kWakeSecondsPerClass`). HRN picks among
  candidates. If the ground queue wins on HRN, no new arrival joins an existing sequence (it drains, leaving a hole),
  and a first one is only cleared if beyond the departure gap. A departure is cleared when the runway is free and the
  next arrival ETA is above the departure gap. Line up and wait (behind landing or departing traffic) needs the next
  arrival beyond the departure gap; a lined-up airplane is released as soon as the runway ahead is clear, with no
  arrival check (if it hasn't lifted off when the arrival reaches the decision height, the arrival goes around; the
  decision height counts only ground occupants). One still short of the runway when an arrival gets within half the
  gap is told to hold short if it can still stop (`CanHoldShort`); one already entering carries on. During a closure a
  lined-up airplane on the runway holds in position until the airport reopens (nobody else uses the runway then).
- Decision height (100 ft, within 2 NM of the threshold): `IsRunwayClearToLand` → go-around (phase-3 pattern) if the
  runway is closed or has an occupant. A ground holder or lined-up airplane not yet on the runway is revoked to
  hold short instead.
- Closure (`UpdateClosures`, each tick): an active `WeatherPhenomenon` (Duration start ≤ frame < end) whose
  radius + 1 km reaches the runway segment, or `InDanger` (end frame not passed) on the airport building. Effects
  (`CancelForClosure`): no grants or line-up releases; airborne cleared arrivals on the runway lane go around (then hold
  at the end of downwind, since nothing is cleared), other airborne ones revert to Waiting (hold); ground clearances not
  yet on the runway are revoked (hold short); pushback approval waits (`IsAtClosedAirport`, lane owner root = airport).
  Options (group "Airport closure"): close for tornadoes / hail / other disasters (kind from the event prefab name via
  `RefreshHazardKinds`; InDanger events are filtered the same way), and an optional closure time limit (toggle, off by default = until over; 30 game
  minutes when on): an event that has closed a runway that long is waived (`RunwayState.m_WaivedBy`).
  `AtcSystem.GetClosure` feeds the "Airport closed: <event>" banner (event prefab name, translated with
  `Assets.NAME[...]` in JS).
- Wind is not usable (static, for pollution and wind power). The practice go-around option was removed.
- Ground queue deadlock (seen 2026-10-17): the nearest ground airplane by path distance can be stuck behind another
  airplane waiting for the same runway (it heads for a farther entry along the same taxiway); cleared, it never moved
  and was re-cleared after every timeout. `m_HeldBehind` (who the taxi spacing / a pushback held each airplane behind,
  also printed as "held by the mod behind ...") now makes `GrantRunways` skip such candidates, and
  `UnblockGroundQueues` passes an already given clearance on to the airplane ahead.
- Apron deadlocks (arrival waiting for an occupied gate, others stuck behind it): the mod's taxi holds (spacing, giving
  way to a pushback) now give up after three simulation minutes behind the same airplane (was one) (`MayHoldBehind`, logged as
  "taxi hold released"), handing over to the game's own traffic logic. A better fix is being considered: hold an
  arrival short of the apron while its gate is occupied (gate blocks, like railway signals).
- HRN keeps the go-around airplane's original request time. The highest-ratio arrival keeps its turn even when it
  can't fit behind the sequence yet (others don't join, so the sequence drains); otherwise a go-around airplane near
  the runway would starve. Its ETA follows the remaining pattern legs, or the final-approach start while holding.
- Temporary testing aid (`DisasterTestSystem`, options group "Testing"): starts a chosen weather event at the selected
  airport (or the first one), like the developer panel's "Start event". Remove before release.

## Phase 4-2 – ATC radio text (implemented 2026-10-17)

- The job records compact `AtcMessage`s (`Say`): each airplane's latest one (`m_LastMessages`, forgotten after about an
  in-game hour) and a 64-entry radio log of all airports. Words are built on the main thread only when a panel shows them
  (`AtcRadio.Describe`).
- Messages: hold short (plain, for landing traffic, for closure), line up and wait, cleared for takeoff, cross runway
  (path leaves the runway before any CarLaneFlags.Runway lane), hold at present position (altitude), cleared to land
  ("number two" from `RunwayState.m_ArrivalCount`), go-around (left/right traffic), extend downwind, landing clearance
  cancelled (closure), pushback approved. `Flight.m_Told` keeps hold messages to one per wait.
- Broadcasts (FAA "attention all aircraft", no readback): "Attention all aircraft, <airport name> Tower, airport closed
  due to <event>." when an airport closes, "..., airport open." when it reopens (once per airport, from
  `UpdateClosures`). Heard on every frequency, each controller naming its own facility ("<airport> Ground", "<airport>
  Tower", "<airport> Approach"). ("<Airport> traffic" is the pilots' CTAF self-announce, not a tower call.)
- Callsign: route name without spaces + the airplane's number on the route, assigned by the mod (lowest free number,
  kept while the airplane stays on the route; not saved). No route: "TRAFFIC nnn".
- Frequencies: approach (holds, "cleared ILS runway 07 approach, contact tower 118.300" when sequenced, approach
  clearance cancelled), tower (landing clearance once the arrival is on final, i.e. on the runway lane; takeoff, line
  up, go-around, ...), ground (pushback approved, "runway 07, taxi" when a departure stands ready with its route onto a
  runway (always before its runway request: at small airports the stand can be within
  the request distance, so the tower request waits for the taxi clearance and the end of the pushback), "taxi to the gate" after landing). Labels like "TWR 118.300" / "GND 121.750" / "APP
  125.350" are made up from the airport entity (VHF ranges, 25 kHz). Exchanges are listed oldest first (newest at the
  bottom). Only arrivals (`Flight.m_Arrival`) can touch down; a departure used to count as touched down and got "taxi
  to the gate" after takeoff. The airplane box shows the last 5 exchanges on its current
  frequency (as everyone on it hears them) with its own marked; the airport panel shows 4 per frequency.
- Runway designators come from `RunwayNames` (shared with the airport panel, L/C/R for parallels).
- UI: `AirplaneAtcSection` and `AirportSection` both draw `RadioBox`es. They refresh as soon as the radio version (a counter of
  transmissions, read only when the tower job is done) changes. An airplane acts on a clearance one simulation second
  (`kResponseFrames`) after it is given, if it was standing: takeoff, line up, crossing, taxi, pushback; a holding
  airplane turns towards the approach a second later too. Newest exchange highlighted, new ones slide in.
- The test-disaster button creates the event through the EndFrameBarrier (created mid-frame, an event lost its Created
  tag in the clean-up before the game initialized it, so it had no position or duration and never closed anything).
- Extended downwind (fix 2026-10-17: the radio said "extend downwind" while the airplane held): not cleared at the end
  of downwind, it now keeps flying downwind (CircuitLeg.ExtendedDownwind) for up to 2 NM, turning base as soon as it
  is cleared; only then does the tower hold it ("hold at present position, maintain 1,500", HoldInPattern). A tower
  go-around now gives its reason ("traffic on the runway" or the closure); every transmission is logged ("radio ...").
- Departure stuck on the runway (seen 2026-10-18): the game's Blocker of a departure cleared for takeoff was an
  airplane flying the pattern after a go-around (still on the runway lane in the game's state), so it never rolled and
  every arrival went around. `PassAirborneBlocker`: a ground airplane on a runway blocked by an airborne airplane gets
  the game's IgnoreBlocker flag (it only lasts one navigation update), and on blocked updates keeps the speed trend of
  the last free one (`FreeSpeed`). The one deliberate lane-flag write in the mod.
- Deadlock detection (2026-10-18; a departure behind an arrival that waited for the departure's own gate, and two
  airplanes left stuck overlapping after the 3-minute release): each tick every ground airplane gets one wait-for link
  (mod taxi hold > gate occupant > game Blocker). `DetectDeadlocks` follows the links; a cycle releases everyone on it
  for 30 s (`m_Released`): no taxi holds, no gate hold, and the game Blocker is skipped like an airborne one
  (`PassAirborneBlocker`). Logged as "deadlock: A -> B -> A; passing through". Runway hold-short stays.
- Weather go-arounds (option, on): in rain 0.5% + 2.5% * precipitation, snow x1.5, at most 5%, never dry; rolled once
  per approach at the decision height (`Flight.m_WeatherChecked`). Pilot-initiated on the radio (GoingAround: "Tower,
  <cs>, going around." then "<cs>, roger, climb and maintain 1,500, make left traffic runway 07.").
- All airplane prefabs have a 20 degree climb angle: the steep departures are the game's. Left alone (4-4 dropped).

## Phase 4-5 – ground flow control (implemented 2026-10-18)

- `CountGroundTraffic`: each tick, airplanes on the ground with a route (navigation lanes) count towards their
  airport (lane top owner); boarding airplanes have no route and don't count.
- `UpdateGroundDelays`: a delay starts at the airport's limit and ends two below it. The limit is per airport
  (`GroundDelaySetting` on the airport building, airport info panel: on/off checkbox and a -/+ limit 2–20); the
  default is 1.5 per gate (airplane stops counted in `RefreshGateCounts`; 4 gates: 6). No global option; both are broadcast
  ("ground delay in effect, arrivals expect holding" / "ground delay ended"). While delayed, `GrantRunways` clears no
  new arrivals at that airport (already cleared ones land), so departures get the runway.
- Airport panel: amber "Ground delay" banner with the count, under the red closure banner (`AirportClosedSection`).

## Gate blocks (researched 2026-10-17, implemented 2026-10-18)

Idea (author): an arrival does not enter the apron while its gate is occupied, so a departure can always get out.
- A gate is an `AirplaneStop` (a TransportStop entity owned by the airport). Its `BoardingVehicle.m_Vehicle` is the
  airplane boarding there (set by TransportBoardingHelpers.BeginBoarding, cleared at EndBoarding, i.e. before the
  departure leaves: during pushback the stop already looks free).
- A transport airplane's `Target.m_Target` is a route waypoint; `Connected.m_Connected` of the waypoint is the stop;
  the waypoint's `RouteLane` (start/end lane and curve position) is where it parks. A second airplane for an occupied
  stop drives there anyway and waits (BeginBoarding just returns), which is how the apron deadlocks.
- So "occupied" = BoardingVehicle set, or an airplane still on the stand lanes (lane objects on the RouteLane lanes),
  or a pushback in progress from that stand.
- Hold point: the end of the last taxiway lane before the apron. Open question: are apron and stand lanes sub-lanes of
  the airport building (top owner = airport) while the player's taxiways are separate nets? If so the boundary is where
  the path's lane owner changes to the airport. Needs a look at a real airport (log the owners along an arrival's path).
- Radio: ground "hold short of the ramp, gate occupied" / "taxi to the gate" when it frees up.
- Findings from the author's airport (30 landings, 4 gates): every lane on the way is a taxiway net owned by the
  airport (all "TA"), so ownership does not mark the apron. The stand lane (RouteLane) is a lane of a short stand edge
  right off a taxiway node; two gates branch off right after the runway exit, the other two after a chain of ~60 m edges.
  In 4 of 30 landings the gate was already occupied (BoardingVehicle set); in one case the arrival waiting for its gate
  (765814:35) held two airplanes for other gates behind it until the 60 s taxi-hold release. So the hold point has to
  be defined by distance to the stand (clear of the pushback area), and followers for other gates should pass a
  gate-waiting airplane at once.
- Implemented: `HoldForGate` – an airplane taxiing to a gate (its Target waypoint's stop, within 2 km of path) that
  another airplane is boarding at or standing on (lane objects on the RouteLane lanes) stops with its nose 100 m
  (`kGateHoldDistance`) before the stand lane; ground: "hold position, gate occupied", then "taxi to the gate" once
  free. One already closer goes on and waits at the stand as in the game. Followers are not let past (dropped 2026-10-18 at the
  author's request: overlapping airplanes don't fit the mod); only the 3-minute taxi-hold release does that, as a last
  resort.

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

## Phase 3 – traffic pattern (implemented 2026-10-17)

Research (FAA): the traffic pattern is a VFR procedure. Turns are left by default (14 CFR 91.126); large or turbine
airplanes fly it at 1,500 ft AGL (91.129, AIM 4-3-3). IFR airliners fly straight-in instrument approaches, and RKSS
arrivals on Flightradar24 do too. After a go-around they climb on runway heading and are radar-vectored around a
pattern-shaped circuit back to final. Departures follow the SID or a heading; they do not fly pattern legs.

Design agreed with the author: arrivals keep straight-in approaches. The pattern is the go-around re-approach and is
reused by phase 4. Nothing is pre-generated. `GoAround` builds the waypoints from the flight's own runway entry
(threshold, landing direction): upwind end = runway end + max(0.5 NM, turn radius); crosswind and downwind at the
pattern width (option, 1.5 NM); downwind end = final length (option, 2 NM) before the threshold; altitude = threshold
+ 1,500 ft. Left by default. Right-hand is set per runway end in the airport info panel (`AirportSection` lists
RWY designators from the landing heading). It is stored as `RunwayPattern` bits on every edge of the runway: bit 1 =
landing with the edge curve, bit 2 = against it. The author asked for per-runway rather than a global or per-airport
setting. Only the ends actually used are listed (`RunwayGeometry.m_Landings`, from the direction of the runway lanes,
because game runways are one-way). Bits shifted by 2 mark the player's choice. Without a choice the default is
right-hand when a parallel runway lies to the left (outside patterns, as at large airports), shared by the UI and
the tower through `RunwayGeometry.IsParallelOnSide`. Parallel runways sharing a number get L/R (L/C/R for three) by
position seen in the landing direction, as at real airports (the author asked for this).

Bug fixed 2026-10-17 (practice go-around deadlock): below about 100 ft the game has already switched the approach
from Approaching to Landing-only. In that state, being below the target (our pattern altitude) counts as touchdown and
drops `Flying`, so the climbing airplane became a "ground" occupant of the runway forever: ground traffic held
short and arrivals held. `GoAround` now restores `Approaching` (the one lane-flag write), and any pattern or holding
flight that loses `Flying` is handed back to the game. A parallel runway (from `RunwayGeometry`, built in
RefreshRunways) on the pattern side flips the side.

At the end of downwind, `EndCircuit` starts a hold at present position whose resume point is the final-approach start
(on the centerline at glide height, lined up). Once cleared, `ReturnToPath` flies base and joins final, and the game's
own Approaching|Landing logic lands it. The game state stays "on the runway lane, Approaching|Landing" throughout the
circuit, so lanes and flags are never touched. While circuiting or holding, the airplane is not counted as on the
runway; it re-requests (Waiting, keeping its request time for HRN). (The phase-3 "practice go-around" option was replaced by the
real decision-height check in phase 4-1.) Pattern and holding speed: at least the speed at which the move
system's minimum pitch, lerp(-climb, slowPitch, (1-f)^2), is level. Near the minimum flying speed the airplane would
otherwise fly nose-up like in the flare (the author reported this).
