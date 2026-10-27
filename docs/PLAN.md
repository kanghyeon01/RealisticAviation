# Realistic Aviation – plan and design notes

Status as of 2026-10-19. Game version 1.6.2f1. Author: Boing787 (kanghyeon01).

## Phases

| # | Phase | Status |
| --- | --- | --- |
| 1 | Tower control (ATC) | **Done, tested in game** |
| 2 | Pushback | **Done, tested in game** (approval wait not yet seen) |
| 3 | Traffic pattern (upwind, crosswind, downwind, base, final) | **Done, tested in game** |
| 4-1 | Go-around at decision height, arrival sequencing, disaster closure | **Done, tested in game** (go-around 2026-10-17, closure with the test disasters 2026-10-19) |
| 4-2 | ATC radio text (callsign, instruction, readback) in the info panels | **Done, tested in game** |
| 4-3 | Airspace: automatic class B/C/D per airport, contact on zone entry, holds separated by altitude per class, class D without approach control, pattern side away from neighbours | **Done, tested in game** (departure check-in seen 2026-10-19) |
| 4-4 | Departure and climb procedure | **Dropped** (the steep climb is the game's 20 degree climb angle for every airplane; the author prefers not to touch game defaults) |
| 4-5 | Ground flow control: when too many airplanes taxi or wait on the ground at an airport (not counting ones at gates), approach stops clearing new arrivals (they hold, "expect further clearance, ground delay") and departures go first; threshold an option (fixed count, or from the gate count if it can be found) | Implemented 2026-10-18 (per-airport limit in the airport panel, default 1.5 per gate, hysteresis 2; off by default), not yet seen in game |
| 5 | Divert (base mod option, on) after 3–5 go-arounds (default 4) or after holding longer than a limit (10–60 game min, default 30; a closure still means holding first): proceed to the next stop of the line | Dropped 2026-10-19 after a trial (see Phase 5 below) |
| 6 | Compass add-on: a standalone compass mod (map north, optional on-screen compass), detected by RA for runway designators | Map Compass built as a standalone mod, seen in game 2026-10-18; RA does not depend on it (2026-10-19) |
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
  radius + 1 NM (1,852 m; was 1 km until 2026-10-19) reaches the runway segment, or `InDanger` (end frame not passed) on the airport building. Effects
  (`CancelForClosure`): no grants or line-up releases; airborne cleared arrivals on the runway lane go around (then hold
  at the end of downwind, since nothing is cleared), other airborne ones revert to Waiting (hold); ground clearances not
  yet on the runway are revoked (hold short); a departure on the runway with a takeoff clearance that is not rolling
  yet (not in the TakingOff state with wheels rolling above 5 km/h, so also one still entering) gets "cancel takeoff clearance,
  line up and wait": it goes back to the line-up clearance, waits lined up and is released as usual when the airport
  reopens (only if another airplane holds the line-up slot does it hold in position) (fix
  2026-10-19: it used to take off during the closure); pushback approval waits (`IsAtClosedAirport`, lane owner root = airport).
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
  airport (or the first one), like the developer panel's "Start event". Removed 2026-10-19 (release cleanup; in git
  history before that date if a test needs it again).

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

## Phase 4-3 – airspace (implemented 2026-10-18)

- Airplane airways are one grid for the whole map (AirwaySystem: 14 x 14 cells of ~989 m at 1,000 m), shared by every
  airport; airplanes ride it and then the airport's own approach lanes. There is no airspace boundary in the game.
- Simple design (author: maps are small, keep it rough): each point belongs to the nearest airport (centre of its
  runways), so neighbouring zones split on the bisector without a radius option. A hold that would start in another
  airport's zone is moved back across the boundary into the airplane's own; the default pattern side turns away from a
  neighbouring airport (a side the player set stays). Contact distance unchanged. One airport: no change.
- Agreed refinement (2026-10-18, not implemented yet): airspace class per airport, B / C / D, chosen in the airport
  panel; default from gates (8+ B, 4+ C, else D). Zone radius by class, scaled for small maps (proposal: B 5 NM, C
  3.5 NM, D 2.5 NM). A point belongs to the airport with the smallest distance / radius (higher classes reach further
  into overlaps). The tower contact distance option is removed: an arrival calls once it is inside its own airport's
  zone plus 0.5 NM, and holds there if not cleared. Class D has no approach control: holds, sequencing and approach
  messages are on the tower frequency ("continue approach runway 07" instead of "cleared ILS approach, contact
  tower"), and the panel shows no APP box. Hold relocation out of other zones and the pattern side rule stay.
- Final 4-3 design (2026-10-18, after the author's test map: 14 km map, 5 airports 3.8–5 km apart, the middle one
  with two parallel runways): class is automatic (2+ runways or 8+ gates: B, 4+ gates: C, else D), shown in the
  airport panel. Contact = inside the own airport's radius + 0.5 NM, whatever the overlaps (it must come before the
  2 NM final). Overlaps are split by distance / radius but only used for the pattern side. Horizontal hold relocation
  is dropped (zones of ~2 km can't hold a 1–1.5 NM racetrack): holds are separated by altitude instead, one band per
  class with B highest (as in reality: satellite airports' traffic stays under the hub's). Class D has no approach
  control. A summary log (class, radius, runways, distances between airports) comes with it.
- Implemented as designed: `AirportSpace` / `RefreshAirspaces` (with the runway map; logs "Airspace of airport ..."),
  `IsWithinContact` (air scan reaches 20 km along the path; the contact distance option is gone), hold altitude =
  runway + class base (D 2,000, C 4,000, B 6,000 ft) + 1,000 ft per stack level, `FrequencyAt` (approach messages on
  the tower frequency at class D, "continue approach runway 07"), `AirspaceRules.IsNeighbourOnLeft` (nearest other
  airport within 8 km; used by the tower and the airport panel).
- Fix 2026-10-18: `RefreshAirspaces` ran before the runway list was rebuilt, so after loading a city no airport had an
  airspace (no "Airspace of airport" log lines); it now runs right after "Found N runways".
- Airspace infoview (2026-10-18, `AirspaceOverlaySystem`): an InfoviewPrefab created in Mod.OnLoad (group 100, the
  game's airplane icon; an infoview needs an infomode, so it highlights airplane stops). While it is active the rings are
  drawn with OverlayRenderSystem, projected on the terrain, VFR-sectional style: B solid blue (inner surface ring at half
  the radius + outer ring), C the same in magenta, D one dashed blue ring. Esc / another infoview closes it.
- VFR chart infoview (2026-10-19): the airspace infoview is renamed "VFR chart" ("VFR 차트"), and it gains three things.
  - **Altitude labels** (`ChartLabels`): a stroke font (digits, S, F, C) drawn with overlay lines, north-up. Limits are
    typical FAA values above the field elevation ((runway height − sea level) × 3.28) in hundreds of feet MSL:
    - B: 10,000 / SFC inside, 10,000 / 3,000 in the shelf;
    - C: 4,000 / SFC, 4,000 / 1,200;
    - D: "25" between four L-shaped corner brackets, as the chart writes it, and "-25" (up to but not including
      2,500 ft) only where a class B or C airspace overlaps it (first always "-25", before that a
      dashed box).

    Each label goes at the first of 8 directions (SE first) that another airspace does not cover. The labels are
    display only; the tower does not use them.
  - **Built-up areas** (`UrbanAreaMap`):
    - 128 m cells over the 14.3 km playable area count residents (households) and workers (company employees) of
      buildings with renters, so city services such as airports are excluded;
    - a cell is yellow when its 3×3 neighbourhood reaches 2,500 per km²;
    - counted on a worker thread (non-Burst IJob) every 2 s while the infoview is open, and drawn as one strip per row
      run.
  - **Airport highlight** (`AirportHighlightSystem`): the game colours buildings only by kind, and airports share
    "TransportStation" with every station. So the legend entries are BuildingInfomodePrefabs of type None: the game
    colours nothing with them, but they get a palette slot and a checkbox. Right after `ObjectColorSystem`, every
    airport (from `AtcSystem.Airspaces`) and its sub-objects and upgrades get Color(slot, 0): B/D blue, C magenta.
    Trees are left alone, as the game does. This replaces the old airplane-stops infomode.
- VFR chart, second pass (2026-10-19):
  - Legend: airports B/D (blue), airports C (magenta), built-up areas, obstacles.
    - Colour slots: ToolSystem gives index = group × 4 + count. Every colour infomode is in group 2 (only heatmaps
      differ), and the colour arrays hold 101 slots, so four or more entries are fine. The mail infoview has five.
    - A first reading took the `k < 4` clean-up loop for a limit of three. Airports were briefly merged into one blue
      entry because of it, then split again.
  - **Obstacles** (`ObstacleMap`):
    - top-level buildings whose prefab `ObjectGeometryData.m_Bounds.max.y` is at least 200 ft;
    - taken highest top (above sea level) first; one within 600 m of a symbol already shown is not drawn and makes
      that symbol a group, so symbols are always 600 m apart (first a 1,024 m grid, whose neighbours could touch at
      the cell edges and overlap downtown);
    - drawn as on the chart legend:
      - a solid spire: inward-curving legs (quadratic curves, control point at 0.05 / 0.2 of the size) from pointed
        feet, a triangular notch between the feet (0.14 high), filled with 24 square-ended strips and outlined thinly;
      - a dot under the notch and thin light rays round the top;
      - a group is two spires foot to foot, each keeping only its outer rays;
      - bigger from 1,000 ft above the ground, with "MSL" over "(AGL)" in feet to its right.
    - Wind turbines (prefab with `WindPoweredData`) have their own symbol: a mast, three blades and a base line,
      labelled the same. Turbines within 1,200 m of a turbine already in a farm join it (single linkage). A farm of two
      or more is a dashed circle round its turbines (+200 m, at least wide enough for its contents), with the turbine
      symbol and the highest elevation in a box in its middle, as the chart's wind farm. Other obstacles within 600 m
      of a turbine, and turbines within 600 m of another obstacle, are left out. Symbol 100 m high (150 m from 1,000 ft); first 220 / 330 m, which
      hid the shape under thick strokes. Label strings are built once per 2 s refresh;
    - the font gained "(" and ")".
  - **ICAO code** (`AirportCode` on the airport building, 4 ASCII bytes in a uint, saved):
    - set from the panel through the game's own TextInput (it blocks hotkeys while typing; Enter or Esc commits on
      blur);
    - `AirportCodes.Get` gives the entered code, or else an automatic one: the first four Latin letters of the name,
      "ZZAA" otherwise, made unique in entity order after the entered codes;
    - Aviation Weather Add-on references this assembly and uses it for its METAR station codes.
  - **Mode C veil**: a thin magenta ring (20 m) around each class B airport.
    - Radius: B radius + 1 NM = 6 NM (first 8 NM, which lay wholly outside the 7.7 NM wide playable area). Real veils
      are 30 NM.
    - Where two veils overlap, each keeps the part nearer its own airport.
    - "MODE C 6 NM" is lettered along the inside at −120° and 60°. `ChartLabels.DrawTextOnCircle` turns each character
      with the circle, top inwards on the lower half and outwards on the upper half, so it reads north-up.
    - The font gained M, O (the 0 oval), D, E and N. Display only.
- Airspace entry and handoffs (2026-10-18): an arrival's first call on entering its zone is pilot first
  ("Portland Approach, <cs>, level 5,000." (altitude above the field, climbing/descending through when changing)" / "<cs>, Portland Approach, radar contact, cleared into the Class B
  airspace, expect runway 07"; C without the clearance, D to the tower). Ground hands a departure to the tower when it
  asks for the runway ("contact tower 118.300"), the tower hands a landed airplane to ground ("contact ground ...")
  before "taxi to the gate", and a departure to departure control after takeoff ("contact departure ...", or
  "frequency change approved" at class D). Approach to tower was already in the approach clearance.
- Radio phrasing pass (2026-10-19; the departure check-in seen in game the same day, the rest is in TestChecklist 2-1): readbacks of every frequency change end "good day"; first calls report
  the altitude above the field instead of "with you"; after takeoff the pilot checks in with departure (B: DEP, C: APP,
  "<Airport> Departure, <cs>, climbing through 1,200" / "radar contact"); after the approach clearance (B, C) the pilot
  checks in with the tower ("ILS runway 36L" / "runway 36L, continue"); taxi is "taxi to runway 36L". Fix: an arrival
  handed over by a class B departure frequency was logged on APP, so the DEP box stayed empty.
  Fix (same day): check-ins said "level 0" because a departure called departure and the next airport the moment it
  left the runway; it now waits until 1,000 ft above its field (`ZoneState.m_Climbing`, 20 s fallback), then checks in
  ("climbing through 1,000") and only then may request the next airport.
  Also: a departure lifting off (still the tower's flight) got a "TransitContact" at 0 ft from `UpdateZone`; zones now
  skip any airplane with a flight. And "taxi to the gate" no longer counts as a taxi clearance for a route that takes
  off (`HasTaxiClearance`): a boarded departure asked for the runway before its pushback; it still does for a crossing
  on the way in.
- Gate parking diagnostics (2026-10-19, `WatchStands` / `LogParking`, removed again the same day): all 66 boardings stood
  exactly on the stand point with the stand lane's heading, also with no mod intervention since landing; the askew
  parking at the international airport is the game's stand geometry (stops 78754/78755 end their lane at curve pos 0,
  driven in reverse), not the mod. Left as the game does it.
- Overlap at a hold-short line (2026-10-19): a departure released from a deadlock with an arrival drove into the
  departure already holding short (released airplanes ignored every blocker for 30 s), and after the reopen the later
  one was cleared first (same distance, no tie-break). Now a release passes only the airplanes of the same cycle
  (`DeadlockRelease.m_Cycle`, `IsReleasedFrom`), and departures at the same spot (5 m) go in request order.
- Cleared departure that never moved (2026-10-19, RiverPort): taxi spacing held it behind an arrival 8 km out on
  approach (airborne airplanes are listed on the runway's lanes), so the whole runway queue waited 500+ s. Taxi
  spacing (`FindTailAhead`) now skips airplanes whose current lane is flying.
- Handoffs between airspaces (2026-10-18, `UpdateZone`): an airborne airplane is with the controller of the zone it
  is in (`ZoneOwner`: a higher class's inner surface area first, else the smallest distance / radius; it stays with
  its controller while still inside that zone). Crossing into another zone: "<cs>, contact Portland Approach 125.350"
  (or Tower at class D) then the pilot's altitude check-in and "radar contact" (B: "cleared through the Class B airspace";
  D tower: "transition approved"); leaving the last zone: "contact <city> Center 132.450" (outside every airport zone is class A under
  the en-route center; radio only, flight altitudes stay the game's). An
  arrival is handed to its destination when it first calls it and stays there; a departure is with its own airport
  after "contact departure" (silent at class D after "frequency change approved").
- Departure control (2026-10-19): a class B airport has its own DEP frequency (`AtcFrequency.Departure`); after
  "contact departure 126.850" its departures talk on DEP until they leave its zone (`ZoneState.m_Departing`), and the
  airport panel shows a DEP box. Class C approach also works the departures (same APP frequency); class D has none.
- Class override (2026-10-18): Auto / B / C / D buttons in the airport panel, stored as `AirspaceClassSetting` on the
  airport building (saved). Crash fix 2026-10-19: applying it inside the UI trigger (add component + rebuild the
  airspaces) crashed the game natively in cohtml `TypeEnd` while the info panel was written; the button now only queues
  the change (`RequestAirspaceClass`) and `AirspaceOverlaySystem` applies it on the next frame in the rendering phase
  (`ApplyAirspaceClassRequests`, works while paused), then refreshes the panel. Also: `display: inline-block` is not
  supported by the game UI (flex instead). That alone did not fix it: the crash (in v8, called from cohtml `TypeEnd`)
  came from the section writing a different property set after the class changed (the APP box appeared). Every
  info-section write now has the same properties, with `hasApproach` / `hasDeparture` flags (fix 2, confirmed in game 2026-10-19).
- Class thresholds raised (2026-10-18): 2+ runways or 16+ gates B, 8+ gates C, else D, so one-runway regional
  airports are D (with 4+ gates they had all become C).
  Clipping as on a chart (2026-10-18): no line inside a higher class's surface area (inner ring); zones of the same class
  keep the part nearer their own airport and are split by the chord through the ring crossings. Lower classes under a
  higher class's outer shelf stay drawn. Rings are 128 straight pieces (dashed: every other piece).

## Phase 4-5 – ground flow control (implemented 2026-10-18)

- `CountGroundTraffic`: each tick, airplanes on the ground with a route (navigation lanes) count towards their
  airport (lane top owner); boarding airplanes have no route and don't count.
- `UpdateGroundDelays`: a delay starts at the airport's limit and ends two below it. The limit is per airport
  (`GroundDelaySetting` on the airport building, airport info panel: on/off checkbox and a -/+ limit 2–20); the
  default is 1.5 per gate (airplane stops counted in `RefreshGateCounts`; 4 gates: 6). No global option; both are broadcast
  ("ground delay in effect, arrivals expect holding" / "ground delay ended"). While delayed, `GrantRunways` clears no
  new arrivals at that airport (already cleared ones land), so departures get the runway.
- Airport panel: amber "Ground delay" banner with the count, under the red closure banner (`AirportClosedSection`). Off by default (2026-10-19); KO name "지상 항공교통 관리".

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
- On a lane, an airplane reserves the lanes ahead within its braking distance (`ReserveNavigationLanes`); a ground
  airplane can't enter a lane overlapping a reserved one (speed 0, no `Blocker`). An arrival on its runway lane far out
  reserves the runway exit and the taxiways to its gate.
- The UI shows half the internal speed (km/h = internal m/s × 1.8): an approach at internal 150 m/s shows 270 km/h.
  Real turn rates therefore double in game seconds for the same circle over the city (standard rate = 6°/s).
- Zone cells keep an absolute height limit (`Cell.m_Height`, from elevated nets such as the airspace nets);
  `ZoneSpawnSystem` and `BuildingUpkeepSystem` read it, and vacant lots keep their own copy (`VacantLot.m_Height`).
  Marking a block `Updated` makes the game compute its cells again.

## Coding rules (from the author)

- Always do an optimization pass. The tower runs as a worker-thread IJob without Burst.
- English-only code and comments. Ship both EN and KO locales.
- Diagnostics: tower events are logged to `Logs/RealisticAviation.log`. Per-airplane lines (radio, long waits, pushback,
  gate paths) only with the "Detailed log" option, off by default; warnings, deadlocks and airport-wide events always.

## Phase 5 – alternate airport (optional, idea)

Decision 2026-10-18 (author): build it as a separate add-on mod ("expansion") that depends on Realistic Aviation, not
inside the base mod. The base mod then exposes a small public API: per-airplane state (go-around count, controlling
airport), per-airport airspace data (class, centre, closure, ground delay), and a way to add radio messages (e.g.
"diverting to ..."). The add-on references RealisticAviation.dll and lists the base mod as a required dependency on
Paradox Mods; it changes the airplane's destination/path, and the base tower follows the new runway as usual.

Research 2026-10-19 (TransportAircraftAISystem):
- A line airplane's `Target.m_Target` is a route waypoint; `VehicleUtils.SetTarget` changes it and marks the path
  obsolete, so the game re-paths from where the airplane is (in the air too). The base tower then sees a new runway.
- At the end of its path the AI boards at the waypoint's stop; if the target is not a waypoint of its route it calls
  `ReturnToDepot` (drops the route, flies to its owner depot). So a line airplane can't simply be sent to an airport
  that is not on its line: it would leave the line.
- The game itself skips a waypoint (`SetNextWaypointTarget`, used when a stop can't take it): passengers stay aboard
  and ride on; that is the low-risk "divert" (proceed to the next stop of the line).
- A real alternate landing (land at an off-line airport, wait, fly back to the destination) needs the add-on to own
  the airplane between those points and keep the AI from returning it to the depot: higher risk, later.
- Dummy traffic (DummyTraffic flag, no passengers) is deleted at its path end, so it can be sent to any airport.
- Later decision 2026-10-19: with only the low-risk divert it goes into the base mod after all, as an option (on,
  divert after 3–5 go-arounds, default 4, or after holding longer than 10–60 game minutes, default 30; a closed
  destination still means holding first, as agreed for 4-1). It is the first base feature
  that changes an airplane's target, hence the toggle. No public API or add-on needed for it.
- Decision 2026-10-19 (author): only the low-risk divert. A line airplane that gives up proceeds to the next stop of
  its line (the game's own waypoint skip; passengers ride on), radio "unable to land, proceeding to <next airport>";
  (dummy traffic is through traffic between outside connections and never lands, so nothing to do there). Landing at an off-line alternate and unloading passengers there was
  considered and dropped as too risky (the AI returns off-line airplanes to their depot).
- **Dropped 2026-10-19 (author) after a trial implementation (not committed).** It was built as planned (options,
  go-around and holding counters, next line waypoint at another airport as the `Target` with the path marked obsolete,
  pilot-first radio "unable to land, request diversion to <next>") and tested in game. Why it was removed:
  - Divert after go-arounds can't be made safe: after a go-around the game keeps the airplane on the runway lane in its
    approach/landing state until it lands. Re-pathing from that state broke the flight: one airplane was cleared to
    land at the new airport and then flew off in a wrong direction out of the map. Blocking diverts from that state
    leaves the go-around trigger with nothing to act on; fixing it would mean rewriting the game's lane state.
  - Divert after holding worked (8 of 9), but most lines run between a city airport and an outside connection, so the
    "next stop" is off the map and the airplanes just flew away, which looked wrong and gained little.
  - Without it nothing gets stuck: during a closure arrivals hold and land when the airport reopens.
  - In the same test one airplane went around four times for "airport closed": the author had started the test disaster
    several times in a row (not a flickering closure).

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

## Phase 6 – compass add-on (agreed 2026-10-18, implemented 2026-10-19)

- A separate, standalone compass mod: the player sets which way map north is (optionally a magnetic variation), with
  an on-screen compass that can be turned on and off. It knows nothing about Realistic Aviation and exposes one public
  value, the north bearing.
- Realistic Aviation detects it loosely (reflection, no assembly reference, not a required dependency): when it is
  installed, `RunwayNames.Designator` adds its north offset, so runway numbers, L/C/R names, the airport panel and the
  radio all follow; without it, map +z stays north as now. Only the names change, not any behaviour.
- Chosen over the compass mod detecting RA: the dependency points the natural way (RA uses a generic north), the
  compass stays reusable by other mods, and the designator rules stay in one place.
- Implemented 2026-10-19 as the separate mod "Map Compass" (repo ../MapCompass): options map north (0–359°,
  clockwise from the game's north), magnetic variation (−30..30°, east +), show compass; an on-screen rose in the top
  right that turns with the camera (`CompassUISystem` from `CameraUpdateSystem.direction`). API `MapCompass.CompassApi`
  (`TrueHeading`, `MagneticHeading` of a map x/z direction). RA: `RunwayNames.Heading` finds `MagneticHeading` by
  reflection once and uses it for the designators; without the mod, map +z stays north.
- Changed the same day (author): Map Compass has no north or variation options any more (only "show compass star",
  now a four-pointed star); north is always the game's. Without Map Compass RA numbers every runway 10, with L/C/R by
  position when an airport has several (`RunwayNames.NameWithoutCompass`); one runway used in both directions is
  "10" both ways.
- Takeoff clearance near the runway (2026-10-19): departures were cleared for takeoff right after their pushback (asked
  at ~550 m, the runway free). A ground airplane is now a candidate for the takeoff, line-up or crossing clearance only
  within its hold-short point + 100 m (`ClearanceDistance`). And (author) a departure calls the tower ("contact tower",
  the runway request) only once it has started taxiing (faster than 1 m/s after "taxi to runway"), not from the stand
  while it pauses after the pushback (`HasTaxiClearance`).
  Fix found in the log (2026-10-19): a departure whose last message was left over from the air (`TransitContact`)
  asked for the runway from its stand before its pushback, as any message but "taxi to the gate" counted as a taxi
  clearance. Now only "taxi to runway" (moving) does, or "taxi to the gate" for a crossing on the way in; anything else
  gets its taxi clearance first (`ClearToTaxi`).
- Departure starvation and queue overlap (2026-10-19, RiverPort log): departures waited 8–10 min at the hold-short
  line under a steady arrival stream (a "first arrival far enough out" was cleared before the runway was free, and the
  hole was gone by then), and the 3-minute taxi-hold timeout then let the queue behind them roll into each other
  (10 releases). Now that arrival is cleared only while the runway is free (the departure goes in the same tick), and
  a taxi hold behind an airplane waiting in line (ground request, itself held, or pushing back) is never given up
  (`IsWaitingInLine`); cycles are still released by `DetectDeadlocks`.
- Close-airplane diagnostics (2026-10-19, research; removed for 0.1.1): the user has seen airplanes
  end up nose to tail since the first versions. `LogCloseAirplanes` logs ("close: ...") any ground airplane whose nose is
  within 8 m of another's body ahead (by position), once a minute per pair, with both airplanes' request, lane (runway
  or taxiway, flags), mod holds and releases, game blocker and geometry. Suspects: no taxi spacing on runway lanes,
  wrong asset bounds, merging taxiways.
  Result (00:03–00:08 log): most entries were airplanes at outside connections (100–190 m/s on "Connection" lanes
  without the flying flag), now filtered out (`IsTaxiing`). The real ones: an airplane rolled up to 4–8 m of one
  holding short whose tail sat across a junction; it was on a crossing lane (game blocker type Crossing), which taxi
  spacing never looked at. Fix: `FindCrossingAhead` reads the game's `LaneOverlap` of each lane ahead; an airplane within
  20 m of the junction point on a crossing or merging lane counts as the airplane ahead there (runway lanes excluded).
  Follow-up the same night: two departures reaching a merge together each took the other as the airplane ahead, the
  wait-for cycle was released as a deadlock and they drove through each other (twice). Now only the one further from
  the junction (by body; tie by entity index) gives way (`GivesWay`), so there is no cycle; one standing across the
  junction always goes first.
- Weather go-arounds by kind (2026-10-19, author): 0.3% in clear weather; per kind the chance grows with the 0–1
  intensity (`ClimateSystem` precipitation/isSnowing, fog, thunder) from 0.3% to 8% rain, 12% snow, 12% fog, 15%
  thunderstorm; the highest kind counts (never above 15%). Logged when it changes by 0.5 points (`GetSettings`).
- Line up and wait default by class (2026-10-19, author): on at B and C, off at D (`LineUpSetting.DefaultFor`). A choice
  in the panel is stored with the class it was made under and holds only while that class stays; any class change
  (chosen or automatic) brings back the default. The old `LineUpAndWaitAirport` tag is no longer read (kept so old
  saves load).
- Final decision the same day (author): Map Compass stays a standalone mod (the "4"-shaped north sign only) and RA
  does not look for it: RA numbers runways from their map heading itself, with the game's north, as before. The
  reflection lookup and the "10 without the compass" rule were removed again.
- Runway designators, FAA style (2026-10-19, AC 150/5340-1): the whole number nearest a tenth of the heading (1–36,
  north 36; a heading ending in 5 may go either way, the code rounds half to even); written with two digits as
  ICAO does ("09", spoken "zero niner"; the author preferred it to the FAA's "9", 2026-10-19); parallels L/R or L/C/R; four or five parallels are two groups, the next-nearest
  number for the two ends on a fixed side (heading 327°: 32L, 32R, 33L, 33R) so the ends stay reciprocal; more are
  numbered. One rule (`RunwayNames.Name`) for the panel and the radio.
- The game's own north (research 2026-10-19): `PlanetarySystem` places the sun with `TopocentricCoordinates.
  ToLocalCoordinates` = (sin az, cos alt, cos az) with the azimuth from north towards east (SunCalc + pi), so map +z is
  north and +x east: the sun rises towards +x. RA's map north was already the same.
- VFR chart symbols, final (2026-10-19). The shapes were approved one by one against the FAA legend in
  `docs/vfr-symbols-preview.html`, and `ObstacleSymbols.cs` uses the same unit coordinates. Solid shapes are cut once
  into 40 rows of fill strips (even-odd scanlines) and edged with a thin outline, which also rounds the corners. Dots are
  overlay circles. The old line-drawn spires in `ChartLabels` are gone.
  - Symbols and sizes:
    - tall spire (1,000 ft AGL and over, 150 m) and its group: two rods, two notches with a dot each;
    - low ^ (under 1,000 ft, 100 m) and its group M: even-width bands;
    - lightning rays: five thin lines with a small jog;
    - wind turbine (110 m): a solid nacelle dot with a gap round it, blades at 90/200/340°;
    - turbine group: two overlapping turbines;
    - wind farm: dashed circle with the elevation in a box.
  - Rules (FAA Chart Users' Guide):
    - shown above 200 ft AGL, or above 299 ft inside a built-up (yellow) cell, from `UrbanAreaMap.IsUrban`, which is
      false while built-up areas are off in the legend;
    - rays only above 700 ft AGL, never on turbines. This follows AC 70/7460-1M, which allows medium-intensity lights up
      to 700 ft; the threshold was read from secondary sources, not the AC text itself;
    - groups (600 m declutter) show the MSL only, as in congested areas;
    - turbines chained within 1,200 m: 1 is single, 2–4 within 1 km is a turbine group, more or wider is a wind farm.
- VFR chart terrain (2026-10-19, `TerrainChartMap`, legend entry "Terrain elevations" / "지형 표고"). The game has no
  terrain-height heatmap, so terrain is drawn as overlay strips like the built-up areas.
  - Sampling: 32 m cells over the playable area. A non-Burst job samples `TerrainSystem.GetHeightData()` and the water
    surface (`WaterUtils.SampleDepth` > 0.5 m means water, left uncoloured). It registers itself as a CPU height reader and
    a water surface reader. It runs when the infoview opens and every 30 s.
  - Elevation tints use the chart bands: < 1000, 2000, 3000, 5000, 7000, 9000, 12000 ft and above, alpha 0.8. They are
    drawn under the built-up yellow, whose alpha rose to 0.7.
  - Spot elevations: local maxima over 1 km (checked every 4 cells), standing at least 100 ft above the ring 1 km out,
    kept highest first and 1.5 km apart. Each is a black dot (20 m) with the MSL in feet (45 m text).
  - MEF: 4 × 4 quadrants (3.6 km). Each takes the highest terrain or obstacle (`ObstacleMap.HighestTop`, the obstacles
    found, so terrain plus anything over 200 ft; trees are left out), rounded up to 100 ft. It is drawn blue: the thousands
    420 m high, the hundreds 250 m and raised. Texts are rebuilt only when a figure changes.
- Built-up areas at 32 m (same day). A cell is built-up when the density over the 13 × 13 cells round it (416 m, from a
  summed-area table built in the job) reaches 2,500 per km². The finished flags are copied to a managed array for
  `IsUrban`, so the obstacle rules never read what the job writes.
- VFR chart, performance and fixes after the first in-game look (2026-10-19). The frame rate dropped, and the city's low
  hills showed almost no tint.
  - Computed once per opening: built-up areas, obstacles and terrain are each counted once when the chart opens, as a
    chart is printed rather than kept live; reopen it to refresh. Before this, built-up areas and obstacles were counted
    every 2 s and terrain every 30 s. Obstacles wait for the built-up flags, so the 299 ft rule always sees them.
  - `RectMerger` merges equal runs down the rows into rectangles, each drawn as one projected line as wide as the
    rectangle is tall. Terrain tints and built-up areas now draw a handful of shapes per even area instead of one per
    32 m row.
  - MEF obstacle tops are looked up only when `ObstacleMap.Version` changes, no longer every frame.
  - Elevation bands are fitted to the map: sea level to the highest land in the chart's eight tints, each step a whole
    hundred feet and at least 200 ft.
  - Wind turbines: the chain distance is 600 m (was 1,200 m). 2–4 turbines make a turbine group, 5 or more a wind farm;
    the 1 km extent rule is gone. Four scattered turbines in an industrial area had been drawn as a farm the size of the
    whole area. The farm outline is now the turbines' convex hull moved out by 150 m with rounded corners (monotone
    chain), dashed evenly along its length, instead of a circle.
- Radio fixes (2026-10-19, from the log):
  - Routeless "TRAFFIC" airplanes on the ground lanes of an outside connection were given "taxi to runway" on a ground
    frequency. Their "airport" was the outside connection (entities 1356069–1356076). Ground handling (`ClearToTaxi`,
    hold, gate hold) now needs the airplane's lane to belong to a real airport, one in `m_Airspaces`.
  - Zone ping-pong: entering and leaving a zone both used its radius, so TRAFFIC 159, skimming the edge, got "radar
    contact", "contact center" 0.2 s later and "radar contact" again 3 s after. It now leaves only 0.5 NM beyond the
    edge (`kZoneExitMargin`).
  - The airplane panel hides its radio box once the airplane was released to a controller the mod doesn't model:
    "contact center", or "frequency change approved" at class D.
- Public API for Aviation Weather Add-on (2026-10-19), main thread only:
  - `AtcSystem.TryGetAirspaceClass`;
  - `GetLandingRunways`: the landing ends in use, named as on the radio and sorted;
  - `IsClosed`: whether the airport is closed and what closed it.

  ATIS phase 3 builds its broadcast from them. Phase 4 ("with information X" on first contact) will need a hook in the
  other direction: RA cannot reference ATIS, so ATIS will register a provider for RA to call.
- ATIS phase 4 (2026-10-19): "with information X".
  - Link: `AtisLink.Current` (replaced by `Api.RegisterAtisProvider` on 2026-10-20), a delegate Aviation Weather Add-on registers. RA cannot reference ATIS, so the call goes
    this way round. Each tower update `FillAtisStates` copies every airport's state (letter, previous letter, QNH,
    minutes since change) into `m_AtisStates` for the job. Without the ATIS mod the table stays empty and the radio is
    unchanged.
  - First calls carry the letter: the pushback request, the first taxi request of a departure without pushback, and an
    arrival's InitialContact (approach, or the tower at class D). Transit traffic (TransitContact) never mentions it.
    These exchanges become pilot first:
    - "PortLand Ground, X, ready for pushback, with information Bravo." / "X, pushback approved."
    - "PortLand Ground, X, ready to taxi, with information Bravo." / "X, taxi to runway 27."
  - Stale ATIS: each airplane listened 2–10 game minutes before its call (fixed per airplane, hashed). If the ATIS
    changed since then, it reports the previous letter, and the controller adds "information Charlie now current, QNH
    1009, ". The message stores the reported and current letters (+1, 0 = none) and the QNH, since the text is built
    later on the main thread.
- Slider ranges centred on the defaults (2026-10-19, the user's request): arrival spacing 15–75 s (was 45–180) and the
  departure gap before arrivals 10–50 s (was 30–300), both in 5 s steps, so the defaults of 45 s and 30 s sit in the
  middle.
- Test disasters again (2026-10-19), as buttons at the bottom of each airport's panel instead of the temporary options
  group removed in b04e84c. There is one button per weather event prefab, labelled with the game's name. The trigger
  only records the request; `DisasterTestSystem` creates the event over that airport on the next simulation update,
  through the end-frame barrier and with the airport as target, as the game's developer panel does. Used to test
  closures and the ATIS phase 5 weather.
  Removed again the same day, once closures and the phase 5 weather had been tried with it; it can be brought back from
  history.

## Maintenance before 1.0 (2026-10-19)

From a review of the whole mod, done in this order:

- Save format version. Every saved airport setting (`LineUpSetting`, `RunwayPattern`, `GroundDelaySetting`,
  `AirspaceClassSetting`, `AirportCode`) now starts with a version byte, `0x80 | version` (now 1), from
  `SaveFormat.WriteVersion`. Saves from before carry no such byte: their first byte is a bool (0/1), a small number or
  an ASCII letter, always below 0x80, so `SaveFormat.ReadVersion` reads it as version 0 and the old layout is decoded
  from that byte. A later change bumps the version and adds a branch, instead of breaking old saves.
- Detailed log option (options, "Log" group, off by default; `AtcSettings.m_DetailedLog` in the job). Per-airplane
  lines are written only when it is on; Aviation Weather Add-on reads the same option for its raw wind and climate
  values next to each report.
- `AtcSystem` split into partial files, moved without any change (checked by putting the pieces back together and
  comparing with the old file): `AtcSystem.cs` (system, main thread, public API), `AtcSystem.Runways.cs`, and the job
  as `partial struct AtcJob` in `AtcSystem.Job.cs` (fields, `Execute`) plus `.Closure`, `.Scan`, `.Clearances`,
  `.Radio`, `.Steering`, `.Pushback` and `.Pattern`.
- After a game patch: both mods log the game version and their own (the add-on also RA's) right after `OnLoad`, and
  `docs/TestChecklist` section 10 lists a short check to run first.

## Add-on API (2026-10-20, 1.1)

- One public entry point, `RealisticAviation.Api`, split by area (`Airports`, `Runways`, `Flights`, `Radio`, `Events`,
  `Atis`, `Options`); documented in `docs/AddonGuide.md`. Designed whole before release, so that it only ever grows.
  - Reads: airports (`AirportInfo`), runway ends (`RunwayEndInfo`), flights (`FlightInfo`), radio log and words, options.
  - Saved settings, through the same code as the airport panel (`AirportSettings`): class, code, line up, ground delay,
    pattern side.
  - Not saved: closures with a reason (airport or runway), per-airport operating overrides, holds, priority, go-around
    orders, radio posts, callsign and phrase providers.
  - Events from the radio log (`AtcSystem.RaiseApiEvents`, copied first so a handler may post), plus airspace class and
    runway changes from the main thread.
- No add-on code runs in the tower job: `AtcSystem.Api.cs` keeps what add-ons set and copies it into native maps each
  update (`FillApiState`); the job reads them (`AtcSystem.Job.Api.cs`: `SettingsAt`, `ApplyApiGoArounds`; closures in
  `UpdateClosures`; holds and priority in `GrantRunways`).
- Everything public by accident is internal now. Kept public: save components, systems, info sections, `Setting`, `Mod`.
- Aviation Weather Add-on uses only `Api`. Both mods must be released together.
- `../RealisticAviationApiTest`: a development-only mod (never published) that calls every member once a city is loaded
  and logs PASS/FAIL to `Logs/RealisticAviationApiTest.log`.

## 1.1 flight changes (2026-10-20)

- **Glide path option** (`AtcSystem.Job.GlidePath.cs`): join the final course outside the FAF at up to 45°, fly the
  centerline on the airway down the glide path, hand over to the runway lane 2.5 km out (`kRunwayLaneDistance`; at the
  FAF the long runway lane reservation blocked a cargo stand by the exit). Too high to descend at 6°: a hold in lieu of a
  procedure turn at the join point (`FlyDescentHold`). ETAs follow the route via the join point
  (`TryGetGlideRouteDistance`), else the next arrival could join the sequence only once the one ahead had landed.
- **Approach height limits option** (`ApproachZoneSystem`): cells under each approach surface (FAF to runway end, 50 m
  below the glide path, 14 CFR 77.19 width) are lowered every 32 frames; changed surfaces and every load give the
  blocks back to the game (`Updated`).
- **Smooth departure turns option** (`AtcSystem.Job.Departure.cs`): a commanded heading turned at 6°/s towards the
  airway course (cut of up to 45°), handed back on the course from a point just ahead (`m_CurvePosition.x` moved on).
- **Pushback** starts only for an airplane that stood on its gate's stand lane, not while the game holds it at the stop.
- **VFR chart**: towers and pillars of bridges (static objects owned by nets) are obstacles, measured from the ground or
  water below.
- All three options are off by default; a "Reset to defaults" button sits at the bottom of the options page.

## 1.2 design: SID, STAR and IAP (draft 2026-10-21)

Why: the game's airway ends about 2.5 km beside the runway at 1,000 m, and departures are turned onto their airway at up
to 30°/s, so both ends of a flight are forced manoeuvres. 1.2 gives every runway end published-style procedures, scaled
down to the game. References: AIM 5-4 (TAA, Basic T), JO 7110.65 5-9-1/5-9-2 (vectors to final), TERPS 8260.3
(segment gradients), 14 CFR 77.19 (approach surface).

```
departure: runway -> SID -> airway
arrival:   airway -> STAR (option) or direct -> IAF -> IAP -> runway
```

### Options

| Option | Default | Notes |
|---|---|---|
| Instrument approach procedure (IAP) | On | Replaces "Straight-in final with glide path" (it becomes part of the IAP) |
| Glide path angle | 5° (3–20) | Unchanged |
| Standard instrument arrival (STAR) | Off | Enabled only with the IAP |
| Standard instrument departure (SID) | On | Replaces "Smooth departure turns" |
| Building height limits under approaches | Off | Enabled only with the IAP; unchanged |

- New setting names (e.g. `InstrumentApproach`, `StandardDeparture`, `StandardArrival`), so that the new defaults reach
  players whose settings file already holds `GlidePath: false` / `SmoothDepartureTurns: false`. The old names go.
- `Api.Options.GlidePath` and `Api.Options.SmoothDepartureTurns` stay (add-only promise) and return the new options;
  `OperatingOverrides.GlidePath` keeps meaning "fly the IAP at this airport". New members are added for STAR and SID.

### Scale

Lengths come from a reference turn radius r: a standard rate turn at the displayed approach speed, i.e. 6°/s in game
seconds at internal 150 m/s, r ≈ 1.4 km. Angles and gradients are kept from the rules; only lengths are scaled.

| Item | Rule (real) | Game |
|---|---|---|
| FAF | 1,500 ft on the centerline | unchanged: max(1,500 ft / tan θ, 2r) out |
| IF (intermediate fix) | intermediate segment ≥ 5–6 NM, IF turn 90° | FAF + 3r on the centerline |
| IAF left / right | perpendicular to the intermediate course at the IF | IF ± 3r to each side |
| IAF straight-in | the IF itself (IF/IAF) | same |
| Intermediate gradient | ≤ 318 ft/NM, optimum 150 | FAF height + 3r × 2.5 % at the IF |
| Initial gradient | ≤ 500 ft/NM, optimum 250 | IF height + 3r × 4 % at the IAFs |
| Turns at fixes | fly-by, ≤ 90° | path following with a look-ahead of about r |

### IAP (Basic T)

1. Sector by where the arrival is when it is cleared (or leaves its hold), as the TAA areas: behind the line through the
   IF perpendicular to the course → straight-in, direct to the IF/IAF; otherwise the left or right base IAF on its side.
   Intercept at an IAF at most 90° (AIM 5-4).
2. Route: present position → IAF → IF (90° fly-by onto the intermediate course) → FAF → down the glide path → runway lane
   2.5 km out (as in 1.1) → approach speed held until 300 m before touchdown (1.1.1).
3. Heights: each fix has a "not above" height from the gradients above; between fixes the target height follows the
   route with the segment's maximum gradient, never below the glide path once past the FAF.
4. The descent hold (1.1's HILPT) moves to the IF: a racetrack with the intermediate course as its inbound leg, used when
   the arrival reaches the IF too high (AIM: "hold in lieu of procedure turn ... for course alignment and/or descent").
5. ETA = remaining route length / approach speed (replaces `TryGetGlideRouteDistance`), so sequencing stays right.
6. Radio: the approach clearance names the procedure ("cleared RNAV runway 07 approach") as now; no fix names yet.

### STAR (option)

1. From its first call (initial contact) an arrival leaves the airway and flies to its sector's IAF, descending to the
   arrival hold height (class base: D 2,000 / C 4,000 / B 6,000 ft, as now).
2. Holding fix = its IAF. Not cleared yet: it holds there, stacked 1,000 ft apart with the existing stack code, with the
   initial leg (towards the IF) as the inbound course. Cleared: it leaves the hold onto the IAP from the IAF.
3. Without STAR: as now, it holds at present position on the airway and is sent direct to its IAF when cleared.

### SID

1. Straight ahead on the runway heading until the game ends its takeoff phase (about 460 m, above the 400 ft rule).
2. A turn of radius r towards the destination airway, on the side that gives the shorter path.
3. The tangent from the turn circle to the airway course (the airway lane ahead), flown straight; the airway is joined at
   that tangent point ("where the departure path comes closest to its own airway"). If the airway lies inside the turn
   circle, it flies on straight until it can turn onto it.
4. Climb at most 15°, never less than 200 ft/NM, up to the airway height (1,000 m above the ground).
5. Hand-back at the join point from a point on the lane just ahead (as 1.1's departure turn). The path is computed once
   per departure, so departures from one runway towards one airway always fly the same track.

### Main technical risk: the airway is frozen while the tower steers

The game sets `Approaching` only when its steering target reaches the end of the airway (`UpdateNavigationTarget`: next
navigation lane not an airway), and only then moves the airplane onto its runway lane. While the tower steers an
airplane (holds today), the game does not advance its airway (`AtcPreNavigationSystem`). Today the IAP logic starts only
after the airway end, so this never mattered; an IAP or STAR taken over mid-airway would never reach `Approaching`.

- **Plan A (try first):** at the hand-over point on final, fast-forward the airplane's path to the last airway lane:
  current lane = that lane near its end, navigation lanes cleared, `PathOwner.m_ElementIndex` moved past it, so the game's
  next update reaches the runway lane by itself. 1.1's departure hand-back already moves `m_CurvePosition.x` on one lane;
  this goes further (the 1.0 notes warn that editing lane buffers once caused speed jumps), so it is prototyped first
  with temporary trajectory logs.
- **Plan B (fallback):** keep the game on its airway to its end, take over from there (as 1.1), and fly the IAP from that
  point. Simpler, but the arrival flies from beside the runway out to its IAF, and the STAR is not possible.

**Result (2026-10-21): Plan A works.** Prototype in `AtcSystem.Job.Procedures.cs` (`TryFastForwardAirway`), tried when a
held arrival is cleared: the last airway lane is found in the current lane, the queued navigation lanes (the game
queues the next airway lane ahead) or the path elements; the airplane's lane state is moved there near its end. Five of
five arrivals, 5.5–8.6 km from the airway end, got `Approaching` 19–33 s later as they approached, and landed without a
go-around; no speed jumps. The game sets `Approaching` when its target, within a braking distance of at least 1,500 m
of the airplane, reaches the airway end; once `Approaching`, it switches to the runway lane when the target is within
about 350 m. So the tower's steering target is kept 800–1,200 m ahead: close enough that `Approaching` comes right after
a fast-forward, far enough that the runway lane comes only at the 2.5 km hand-over.

### As built (2026-10-21, before the test round)

Where the build differs from the draft above:

- IAP: the descent hold stays at the final course join point (1.1's HILPT), not at the IF. The IF is already flown
  fly-by with the segment gradients, so an arrival reaches the join point too high only from a short procedure.
- IAP: the sector is chosen when the procedure starts (cleared, or leaving a present-position hold); the airway is
  fast-forwarded once then (`m_FastForwardTried`). The tower's target is kept `kProcedureLookAhead` (1,000 m) ahead.
- STAR: the IAF hold starts when the tower tells an arrival to hold (`StartHolding`), not from initial contact; an
  arrival cleared at once flies the IAP from the airway, which is the same route. From a STAR hold the procedure goes
  on at the hold's fix (`m_StarHold`, `m_ProcSide`), not from a new sector choice. The radio still says "hold at
  present position".
- SID (first build, replaced after testing, see below): the heading stepped at 6°/s towards the airway course, cutting at 45° until within
  r × (1 − cos 45°) of it, then at the angle of an arc of radius r that ends on the course (acos(1 − off / r)). With
  the same geometry every time, departures from one runway to one airway fly the same track. Least climb 200 ft/NM
  (`m_MinClimbAngle` floor) below the airway height; at most 15°.
- First test round (2026-10-21): arrivals reached the IF 550–930 m too high (the airway is some 1,500 m above the
  runway; the TERPS maximum descents, 8 % and 5.2 %, are too shallow over the scaled segments) and every one went into
  the descent hold; a held arrival's ETA came from the game's airway beside the runway, so the next could join the
  sequence only once the one ahead had landed (waits of 140–357 s, one arrival every 2–3 min). Fixed: the procedure
  descends at up to `kSteeringSlope` (6°), and an arrival still on its airway counts the procedure route it will fly.
- Takeoff "no delay" (user request, 2026-10-21): "cleared for takeoff, no delay" (FAA phraseology; ICAO says "cleared for
  immediate takeoff") when an arrival in sequence is due within the departure gap + 30 s (`kNoDelayMarginSeconds`).
  `AtcMessage.m_Value` = 1; add-ons read `RadioMessage.NoDelay`. No option: the wording changes, not the behaviour.
- IFR chart (user idea, 2026-10-21): a 1.2 patch after 1.2 ships, reviewed symbol by symbol with the user. First draft:
  `docs/ifr-chart-preview.html` (procedure fixes, tracks and holds at the game's geometry, RNAV airways, green IAP
  airports; stroke font letters A, I, T, '/', '.', '°'). Not built.
- SID loop (test 2026-10-21, cargo route 1): after a 150° turn the departure was 3.7 km off its airway, and the game's
  current lane ended ~2 km past its target; the 45° cut passed abeam of that end, the SID switched to "fly to the lane
  end" with the point inside its turn circle, flew straight away and came back in a 6 km loop. Fixed: the course to join
  is the first of the current, queued and path airway lanes (8 elements) the airplane isn't abeam past the end of; on
  hand-back on a later lane, the lane state moves on to it (`AdvanceDepartureLane`, as the airway fast-forward).
- Holding stack step-down (user request, 2026-10-21): each tick, holding arrivals (not cleared) move down to the
  lowest free level under them, lowest first (`StepDownHolds`), with "descend and maintain N" (`AtcMessageKind.
  DescendInHold`, appended after `Custom` so existing values stay). Arrivals leave the stack from low down instead of
  reaching the IF from 2,000+ m and spending a minute or two in the descent hold. A new hold joins on top of the stack
  (one level above the highest), not in a free level under airplanes already waiting.
- UPS stuck after clearance (same test): airplanes from an outside connection start on their airway lane with
  `TakingOff`; told to hold at once they kept it, and the approach logic skips takeoffs, so nothing flew them and their
  ETA blocked the sequence until the clearance timed out. `FlightScan.m_TakingOff` is now false on airway lanes.
- Final snap (test 2026-10-21, per-tick log): "IF passed" fires at the start of the fly-by turn, 1.4 km before the IF;
  FlyToFinalFix then flew straight to the join point, closed the centerline at 15° and reached the runway lane 45 m off
  and crabbing 14°, so the game snapped it straight (bank -8° then +13° within a second). Past the IF the final logic now
  tracks the centerline (target `kProcedureLookAhead` ahead of abeam) until the join point.
- Departure weaving (same test): (1) the UPS fix made TakingOff false on all airway lanes, but a real departure's first
  climb lane is an airway with TakingOff, so the SID took it right at liftoff; the exception is now in ApplyGlidePath, for
  arrivals only. (2) The airway after the climb was a zigzag of ~1 km lanes (135° and 180° pieces of a 157° route) and
  the SID chased each lane's course; a 2 km look-ahead course still swung ±20° at each bend. The SID is now a turn and
  "direct to" a fix on the airway 4 km ahead (`FindDepartureFix`); pointing at it, the game takes over with the fix as
  its target (`HandOverAtFix` moves the lane state there). After that (`ZoneState.m_SidJoined`) the SID steps in
  again only for bends over 30°.
- SID circling again (same day): the move system turns an airplane at most `AirplaneData.m_FlyingTurning` rad/s
  (AircraftMoveSystem); a 787 manages about 3°/s, so planned with 6°/s the fix looked reachable and it circled it. The
  SID now plans with min(6°/s, m_FlyingTurning), and hands over at its fix after 90 s whatever happens.
- SID jerks (same day): handed over within 10° of the fix, the game turned the rest at its own rate (15-18°/s) in half a
  second; and a path update at a lane change (PathUnknown) ended the turn and gave the game its old target back. Now the
  hand-over waits until the actual heading is within 2° of the fix (`kDepartureAlignedAngle`), and a path update no
  longer ends a turn under way.
- SID, per-tick attitude log (same day): turning was smooth (6°/s, bank 10-14°), but airliners reach ~240 m/s on the
  airway, so the turning circle is 2.3 km, and a fix 4 km along the airway from its nearest point lay inside it for a
  big turn: the airplane flew straight, then circled the fix through 270°; then "too close" picked a new fix from the
  nearest airway point, behind it, and the bank reversed (+14° to -9°). Now the fix is the first airway point from 4 km
  on (500 m steps, up to 15 km) outside the turning circle at the airplane's top flying speed (`IsReachable`), and a
  fix is never re-picked mid-turn (close to it, the game takes over). The straight/turn switch has hysteresis.
- SID after hand-over (same day): turns are now one smooth 6°/s turn (bank up to 14°). Right after a hand-over, the
  rejoin check computed a new fix 4 km on, past a bend just beyond the handed-over fix, and started a turn the other
  way (bank -14° to +4°). Once handed over, the SID now steps in again only when the game's own target is more than 30°
  off the nose.
- Procedures of nearby airports can overlap (decided 2026-10-21: handled in the 1.2 patch together with the IFR chart,
  where the overlaps show). A procedure spans ~10 km out and ±4.3 km beside the runway (5°), beyond a class C/D
  radius; holds are separated only by class band (same-class neighbours share 4,000 ft); SID fixes may cross another
  airport's final. No mid-air collisions in the game, so it is visual only. Ideas: use the other IAF (or straight in)
  when an IAF or its hold lies in another airport's airspace; raise one same-class neighbour's hold band 1,000 ft;
  skip SID fixes over another airport's final.
- Code review notes for the next patch (external review, checked against the code 2026-10-21):
  - VFR chart: obstacles are found once per opening, so turning built-up areas on or off while the chart is open
    doesn't re-apply the 299 ft urban rule until it is reopened. Re-run the obstacle search when that legend entry
    changes. (Built-up areas off = no urban rule is intended.)
  - Closures: an airport's closure reason is the first runway's (`TryAdd`), so an add-on's runway closure can hide a
    disaster on another runway, and the reason isn't updated while the airport stays closed for another cause. Prefer
    the disaster, and update the banner and broadcast when the reason changes.
  - Checked, no change needed: ObstacleMap/UrbanAreaMap Reset while a search is pending (Update completes and disposes
    the lists before starting another; Dispose frees them), HoldingPattern at a turn centre (atan2(0, 0) = 0, no NaN).

### Order of work

1. Prototype the fast-forward (Plan A) on one arrival, with trajectory logs; decide A or B.
2. Fix geometry per runway end (+ ETA), IAP route following, descent hold at the IF.
3. SID.
4. STAR and holds at the IAFs.
5. Options, locales (EN/KO), API members, README, PLAN; remove the temporary logs; version 1.2.

## 1.1.1 (2026-10-21, feedback patch)

Versions: MAJOR.MINOR.PATCH (the patch was meant as a letter, 1.1a, but the game parses the UI module version as a number); new features go to the next minor.

- "Remove this airplane" button option (`AirplaneAtcSection`): marks the airplane's path stuck (`PathFlags.Stuck`), as
  `StuckMovingObjectSystem` does, so `TransportAircraftAISystem` ends boarding and deletes it; logs its state first.
  A stop-gap for airplanes stuck on a runway (forum report); the cause still needs a log.
- Early flare on the long final: on the runway lane the game brakes for the target just ahead and slows to touchdown
  speed at once; near the minimum flying speed the move system holds the nose up. `FollowGlidePath` keeps the
  level-attitude approach speed and reaches touchdown speed `kFlareDistance` (300 m) before the touchdown point.

## 1.2.1 (2026-10-22, hotfix)

- Go-around blocked the runway: GoAround left the airplane on its runway lane, and the game doesn't advance the lanes
  of an airplane the tower flies round the pattern, so for the whole circuit it was the vehicle ahead of everything on
  that lane. The arrival that had landed couldn't roll out and the departure lined up couldn't take off ("blocked by"
  the circling airplane, type Continuing) for minutes; the next arrival found the runway not clear and went around too,
  and so on (log 2026-10-22: every blocker was an airplane that had just gone around). Likely the forum report of an
  airplane stopped on the runway with go-arounds and no arrivals. `LeaveRunwayLane` moves it back to the end of its
  last airway lane (the runway lane is the next path element again, queued lanes dropped), as the airway fast-forward
  does. Uploaded without an in-game test (the user's call: hard to reproduce on purpose).

## Known issue: an arrival already on final when a save loads (2026-10-22; fixed in 1.2.3)

- Seen once: right after loading a save, a departure was cleared and a landing airplane touched down on the runway
  with it (ground overlap, no go-around).
- Cause: an airplane already on its runway lane when the tower starts (on final inside the 2.5 km hand-over, or the
  mod turned on mid-game) is never tracked. `Scan` skips the occupied runway's lanes when it looks for the upcoming
  runway, so `TryRequest` has no runway for it; and airborne airplanes on a runway lane don't count as occupants
  (`UpdateFlight`: only ground ones and TakingOff), so the runway looks free and departures are cleared. Untracked, it
  gets no decision-height check either.
- Possible fix (only if it is reported): count an untracked airborne airplane on a runway lane as an occupant
  (`!scan.m_Flying || scan.m_TakingOff || !hasFlight`), or adopt it as a cleared arrival.

## Next patch: tuning values through the add-on API (decided 2026-10-23)

- Forum request (bouquet1, 2026-10-23): pushback distance per airport. Today it is fixed in the core (half the airplane
  length, 15-40 m) and the API can't change it.
- Decision: simple value changes belong in add-ons, not in the core. The core opens its tunable values as per-airport
  `OperatingOverrides` (add-only, null = the option or built-in value); per-airport settings, their UI and saving live in
  an add-on (a "tuning" add-on), the core panel stays as it is.
- Candidates: pushback distance and speed, taxi spacing, hold-short setback, holding leg length, stack spacing, hold base
  altitude (per class today), IAP / STAR / SID on or off, FAF height, line up and wait.

## 1.2.2 (built 2026-10-23, patch: no visible change)

- API version 2: `OperatingOverrides` / `OperatingValues` gain every option that can be set per airport (IAP, STAR,
  SID, approach height limits, weather go-arounds, holding leg, hold-short setback, taxi spacing, pushback and a fixed
  pushback distance, closure kinds and limit). The job reads them through `SettingsAt` / `SettingsForRunway` instead of
  the global `m_Settings` (closures per airport; SID by the takeoff airport; ground settings by the airport the airplane
  is at, `GetAirportOf`, skipped while no add-on set anything). The climate's go-around chance is kept with the option
  off (`m_ClimateGoAroundChance`) so an add-on can turn weather go-arounds on at one airport. STAR's option is kept as
  set and applies where the IAP is flown. Approach height limits per airport (`GetApproachCorridors`). Detailed log and
  the remove button stay global. A pushback distance over 40 m may reach an arrival waiting 100 m before the stand.
- Review fixes from the notes above: the VFR chart finds obstacles again when built-up areas are switched with the chart
  open; an airport's closure reason prefers a disaster over an add-on's runway closure, and a changed reason is
  announced again (banner follows).
- RealisticAviationApiTest: version 2 members, clamping, clearing.

## 1.2.3 (built 2026-10-24)

- Fixed the known issue above (the user saw it again: the first airplane after loading wasn't handled; log
  2026-10-24 18:56: an airplane got "taxi to the gate" without ever being an arrival). `AdoptLandingAirplane`
  (`TryRequest`): an untracked airplane in the air on a runway lane with Landing/Approaching is taken as an arrival
  cleared to land on that runway (entry at its place on the lane, i.e. the threshold; procedure stage final). From then
  on it counts in the runway's arrivals, gets "cleared to land", the decision-height check and touchdown as any other.
- Also departures (user's screenshots, load at 20:44:27): a Spirit already rolling mid-runway and a UPS standing at the
  threshold when the save loaded were both untracked (the Spirit appears only as a transit contact once airborne, the UPS
  never); the game rolled the UPS before the Spirit had lifted off. `AdoptRunwayGroundAirplane` (`TryRequest`): an
  untracked airplane on a runway on the ground, not landing, is taken as cleared for takeoff and runway holder when
  rolling (≥ kRollingSpeed), or told to line up and wait (runway line-up slot) when standing, so it goes only once the
  runway is free (`ReleaseLinedUp`).
- Test (20:56): the Spirit was taken as rolling, but the UPS was moving too (entering, starting to roll) and so counted
  as rolling; the runway holder was taken, so it was left untracked and rolled. Now (the user's rule) past its line-up point it
  counts as on its takeoff roll, short of it (or still entering) it stops there; of two past it the one further along
  holds the runway and the other is lined up (`RunwayProgress`), whichever the tower sees first.
- Then (user's call): two both past the line-up point both keep rolling; stopping one halfway down the runway and starting
  it again looks stranger. The log says "Serious incident: X started its takeoff roll while Y was still rolling", and the
  one behind gets the FAA Brasher notice, new `AtcMessageKind.PilotDeviation` (appended after `DescendInHold`): "possible
  pilot deviation, advise you contact <airport> Tower at <telephone>." / "Wilco, <telephone>" (a made-up number per airport position
  in the 555-0100–0199 range set aside for fiction, `TowerTelephone`; one area code per city, made up from the city name;
  no option (only this rare call uses it), but add-ons can set it: `Api.Radio.AreaCode`, API version 3). The first one keeps the
  runway holder; the second is cleared but holds nothing, and the ground occupants keep the runway closed until both are
  off it.
- Log check (21:46 / 21:48): every adopted rolling airplane got a "cleared for takeoff" right after, and a short stop:
  adoption set `m_Told`, and told + cleared on the runway is the closure-over path in Steering (clear again, stand still
  for the readback). Rolling ones are now adopted with `m_Told` false: no clearance call, no stop. The phone number's last
  two digits come from where the airport stands, not its entity (the airport was Entity 1754497:1, then 1757281:3 after a
  reload in the same session) nor its name (a name the player didn't give follows the game language). Then (user's call)
  the tower frequencies too: `AtcRadio.AirportSeed`, the airport's position, seeds both (frequencies change once).
- ICAO code tooltip no longer says it is for weather reports (that is the weather add-on): "Add-ons can use it." /
  "다양한 애드온에서 활용할 수 있습니다."

## Next patch (in testing since 2026-10-24): runway vacated only with the tail past the hold-short line

- User's screenshots (23:45:13 / 23:45:49, log 23:44:53–23:45:02): an American 787 that had landed turned onto the exit at
  the threshold; "vacated runway" fired the moment its lane left the runway lane, with its rear fuselage and tail still over
  the runway, and the UPS lined up there got "cleared for takeoff" 2 ms later. The vacate was decided by the lane of the
  airplane's reference point, while entering is held with the nose a hold-short setback before the runway lane.
- Now a ground airplane that leaves the runway's lanes after using them (landed or crossed) stays in `m_RunwayExits` and
  counts as a ground occupant of that runway until it is its tail length plus the hold-short setback (straight line) from
  where it left the lanes; then "is clear of runway (tail past the hold-short line)" is logged. Until then takeoff,
  landing (decision height) and line-up clearances wait as for any airplane on the runway.
- The user tries it for a while; if nothing comes up, it ships as a patch.
- Same patch (user's question 2026-10-24): an arrival at the decision height continued as soon as the departure ahead had
  lifted off. FAA JO 7110.65 3-10-3: the departure must have crossed the runway end, or (distances known) be airborne and,
  category III, 6,000 ft from the landing threshold (ICAO Doc 4444 is the same in principle). User: scale it, the game's
  airports are small, but keep assets/DLC with long runways in mind. Now `IsDepartureTooClose`: a departure in the air still
  on the runway lane must be min(6,000 ft, 60 % of the runway length) past the arrival's threshold (along its entry
  direction), else the arrival goes around ("departure not far enough ahead"). 60 % = 6,000 ft on a 10,000 ft runway; long
  runways get the FAA distance itself. Leaving the runway lane in the air is taken as past the runway end.
- Same patch (forum, Jen1, 2026-10-25): a semi truck's info panel showed the airport section (airspace, ICAO code, radio).
  `Airports.GetAirport` walked the selected entity's owner chain, and a company vehicle's owner is the building that runs
  it, here the cargo airport. Now only structures count (user: an allowlist rather than excluding vehicles and people):
  the main building (a building at the top of its owner chain), net edges and nodes (runways, taxiways), transport stops;
  not upgrade buildings (user, testing: the cargo terminal upgrade shows no airport panel, and it shouldn't; the airport
  itself is selected as one piece). This also fixes the closed-airport banner
  (`AirportClosedSection`) and the weather add-on's ATIS section, which use the same lookup (`Api.Airports.Find`).
- Same patch (user): the pilot's own go-around now gives its reason, as pilots usually do (not required; the tower asks
  otherwise). User: a table of reasons by weather with 0 for the ones that don't happen, so they can be changed freely
  later; "runway not in sight" rare except in fog (on an ILS only in very heavy rain or snow); city-wide.
  `GoAroundReason` (public): unstable approach, runway not in sight, windshear, gusty winds, wake turbulence, birds on the
  runway; new ones at the end. Default weights (`GoAroundReasons`, rows clear/rain/snow/fog/thunderstorm):
  60/0/0/10/20/10, 40/10/10/40/0/0, 40/10/0/50/0/0, 10/90/0/0/0/0, 25/5/45/25/0/0. Copied for the current weather into
  `AtcSettings.m_GoAroundReasonWeights` each update; the job picks by weight; reason + 1 rides in the message value
  above the right-hand bit (0 = none given). API version 4: `Api.Radio.Set/GetGoAroundReasonWeight` (reset on city load),
  `WeatherKind` public; `OperatingOverrides.DepartureAheadMeters` / `DepartureAheadShare` per airport (the landing
  separation values above, defaults 1,829 m and 0.6).

## Next update: IFR chart (design started 2026-10-25)

- Reference: the user's Gimpo (RKSS) AIP charts (AD chart, SID, STAR, instrument approach). Real charts are split: SID
  and STAR charts per runway direction (several procedures each, plan view + coding table), one approach chart per
  approach (plan view, profile, minima, missed approach text, MSA).
- Form (user): mixed. The plan view is an infoview overlay on the game world (projected lines, the VFR chart's stroke
  font extended to A-Z), chosen per airport and runway direction as SID / STAR / approach layers; the approach's profile,
  MSA and coding table go in a small UI window. Computed once and kept in memory, rebuilt only when runways, options or
  add-on overrides change (not baked into images or files: blurry when zoomed, hard to project on terrain, stale files).
- Symbol preview: docs/ifr-symbols-preview.html. Decided with the user so far:
  - Runway: outline (black border, light fill) as on the AIP, not the FAA green bar. Compass rose round the airport:
    shown, still to confirm.
  - Waypoints: procedure fixes an open circle with four points; a fix where a procedure meets an airway an open
    triangle; role in bold brackets above the name, e.g. (IAF). No filled triangles (compulsory points), no VORs.
  - Legs: plain bold line, arrowhead only at the end of each procedure. Distance in an arrow outline open at the back,
    always in the line (the line runs into its back and out of its tip; AIP sometimes moves it beside the line when
    crowded, user chose always in the line). Course beside it with a gap.
  - Holds: an arrow on each straight leg, each leg's course outside, MAX SPD / MNM ALT / 1 MIN inside along the axis.
  - Procedure names: bold, about a letter height off the line, opposite the course; SID named after its last fix, STAR
    after its first.
  - Stroke font A-Z (+ % /) and the altitude notation (underline at or above, overline at or below, both at, stacked
    window): confirmed as drawn.
  - Airways: thin grey line, designator along it beside the line, no box; a second designator on the other side (W61 /
    Z54), as on the AIP.
  - Fly-by turns: drawn as arcs on the inside of the fix with the game's turn radius (cut down when a leg is too short),
    as on the AIP; the legs stop where the arc starts. Only on a single path (SID turn, every missed approach corner,
    the turn onto final with the arrowhead). Where paths merge (both IAF legs at the IF) the lines run straight into the
    fix (user, AIP UD050 / UP050).
  - Missed approach: drawn as the AIP's RNP missed approach (user's pick, RKSS RNP RWY 14L): the whole go-around
    pattern the mod flies as dashed legs with fixes, courses and distances (upwind end, crosswind end at 1 500, downwind
    end abeam the FAF), arrowhead where the base leg meets final at the FAF; MISSED APPROACH text lists the fixes.
  - Obstacles: the VFR chart's own shapes (spire from 1,000 ft above the ground, ^ below, foot dot), terrain a dot, no
    lighting marks; placed by the label placer.
- Open: compass rose, waypoint naming (5-letter pronounceable vs airport code + number), true course only (no magnetic
  variation in the game), MSA in the window only, hold text at small scales, the window (sections 8-10).
- Obstacles (decided with the user 2026-10-25): AIP instrument charts show only obstacles that matter to the procedure,
  ICAO symbols (a tower, rays when lighted; terrain high point a dot), elevation above sea level only ("1 506").
  - STAR and the initial segment (IAF -> IF): clearance under the procedure's minimum altitude there (hold: its lowest
    level) less than the MOC, 1,000 ft (PANS-OPS initial approach; real value kept although the map is small: heights are
    real scale, only the lengths were shrunk). Intermediate (IF -> FAF): 500 ft. None shown when nothing is that close.
  - Final (FAF -> threshold): penetrates the approach surface the height-limit option already uses (50 m under the glide
    path, 1,000 ft wide at the runway end, 15 % splay), floored near the threshold by a 2 % slope (Annex 14 approach
    surface); these are the controlling obstacles.
  - SID: penetrates the 2.5 % obstacle identification surface from the runway end; numbered (1), (2) with a note
    "MNM CLIMB x % UNTIL ... FOR OBST (1)" (PDG = (O - 5 m) / d + 0.8 %).
  - MOC values (1,000 / 500 ft) and the surfaces are add-on tunable through the API.
- Approach window (redone after the AIP's RNP RWY 14L lower half): header (frequencies, aerodrome elevation, heights
  related to the threshold); MISSED APPROACH text with transition altitude/level and the MSA beside it; profile from the
  IAF along the track (altitudes AMSL underlined, heights in brackets, FAF Maltese cross, gradient "8.7% (5.0°)", MAPt at
  the threshold, ELEV / RDH, hatched minimum-altitude blocks per segment = highest obstacle + MOC there, or the MOC; the
  missed approach dashed with its fixes); minima table (LNAV/VNAV DA(H) = threshold + the mod's 100 ft decision height,
  no visibility minima in the game) and the rate-of-descent table (V/V fpm by groundspeed for the glide path); "Circling
  not authorized". The coding table moved to a second tab (on the AIP it is a page of its own).
  Then (user: pick one chart type, don't mix): the RNP chart only. The game's approach is an RNAV "T" with waypoints and a
  vertical path, no ILS, VOR or DME, i.e. RNP APCH with LNAV/VNAV, titled "RNP RWY 07"; the ILS chart's parts (DME
  recommended profile table, shaded glide slope, CAT-I / LOC minima) are left out. Hatched blocks never reach above the
  path: a block is at most the altitude its segment ends at; one that would (an obstacle the procedure doesn't clear) is
  drawn up to the path, outlined red, with a note. Altitude labels go below the path in the gap above the block, on
  the side of the fix line that has room (the FAF's on the final side), never over the hatching.
- Procedure altitudes raised over obstacles (user, 2026-10-25; the red "obstacle above the procedure" mark was mine, no
  such rule exists: real procedures are designed so the blocks stay under the path). Built (not yet tested in game):
  - `AtcSystem.Survey.cs`: per runway end, a job over the initial (IAF -> IF, each side) and intermediate (IF -> FAF)
    areas and round the STAR holding fixes (IAFs, IF; reach = hold leg + a turn diameter): terrain sampled every 200 m and
    building tops (prefab bounds, >= 50 m) -> highest top + the MOC it needs there (primary full, secondary tapering) ->
    `ProcedureFloor` (metres above the threshold). Runs when the runways change and once a game hour, not in the first
    256 frames after a load; results swapped in with the tower job completed.
  - `GetApproachFixes`: IF = max(default, intermediate floor, both initial floors) - one IF altitude for everyone; IAF =
    max(IF + 4 %, its initial floor). FAF unchanged (final obstacles would raise the DA, not the FAF). STAR holds:
    base = max(class hold base, the hold's floor).
  - Airplanes already on the procedure see a change only if a survey lands mid-approach (hourly; rare); not frozen per
    flight.
  - API version 5: `OperatingOverrides/OperatingValues.InitialMocFeet` (1,000), `IntermediateMocFeet` (500),
    `ObstacleAreaPrimaryMeters` (1,000), `ObstacleAreaSecondaryMeters` (1,000), per airport.
  - Chart rule follows: obstacles shown on STAR / initial / intermediate are the controlling ones (the one that sets each
    segment's or hold's minimum altitude); final: those through the approach surface.
- Profile missed approach (user, from the AIP RNP profile): a dashed sketch beside the threshold, out along the runway
  and round the pattern with rounded corners, each leg's course along it (070°, 340°, 250°, 160° from the real legs),
  TORIB / NAMPO / VELOK at the corners, arrowhead at the end; no floating altitude (it is in the MISSED APPROACH text).
  Then (user): courses sit in the line itself on the profile, as the AIP does ("—143°—", "-142°-"): the line breaks round
  the figure, turned along the leg, for the approach path (solid) and the missed approach (dashed); the missed approach
  turns are wide rounded corners.
  Then (user): the final segment's minimum and the runway were missing on the profile. Final block: DA(H) 159 (100 ft,
  the mod's decision height) hatched from the FAF to where the glide path comes down to it, label boxed (the AIP's "430"
  box); the runway as a black bar on the ground line from the threshold, "THR RWY 07" under it; ELEV / RDH moved right.
  Then (user): the final block runs right up to the runway bar; the final is solid with the arrowhead at the minimum, then
  dashed down to the runway; the missed approach starts at the minimum. At true scale the mod's 100 ft decision height
  puts the minimum 0.09 NM from the threshold, so that part is only a few pixels wide. Tried a compressed (power) scale
  and blocks forced to step down; reverted (user: no rules the real charts don't have). Real profiles keep one scale;
  the minimum is visible there because real DAs are higher (CAT I DH 200 ft, LNAV/VNAV higher). Open: raise the mod's DH.
- Decision height (user, 2026-10-26): 200 ft by default (CAT I; it was 100 ft, which put the minimum 0.09 NM from the
  threshold, invisible on the profile), per airport through API v5 `DecisionHeightFeet` (clamped 50–1,000 ft).
  `AtcSettings.m_DecisionHeight` replaces `kDecisionHeight`; the go-around check and the weather go-around happen there.
  Preview: DA(H) 259 (200).
  Then (user, AIP): the sketch runs level from the minimum out past the threshold, climbs round the pattern and ends
  level with the arrowhead at the fix it heads for (VELOK), not diving back down: the base leg to BIROT is left to the
  plan view, as the AIP stops at DOKDO.
- Decided (user, 2026-10-26): waypoint names are pronounceable five-letter names (ICAO 5LNC style, e.g. KOVAN), the same
  every time for the same place; courses true only (the game has no magnetic variation); MSA computed from terrain and
  obstacles in four quadrants round the airport, airspace radius, shown in the approach window only.
  Naming rule to build: consonant-vowel-consonant-vowel-consonant from a hash of the fix's position (rounded, so it is the
  same after a reload, like the frequencies), unique within the city (on a clash take the next hash), a short list of
  words to avoid; airway-end fixes get names the same way.
- Decided (user, 2026-10-26): compass rose round the airport, as on the AIP. Opening the chart: an "IFR chart" infoview
  like the VFR chart's (AirspaceOverlaySystem: infomodes as checkboxes) with SID / STAR / approach toggles for the map
  layers; the one-page chart (approach window: profile, minima, coding table) chosen from a new selector in the airport
  info panel (e.g. RNP RWY 07, per runway end in use).
- Infoview chart choice (user, 2026-10-26): one checkbox per chart, "airport code + chart name" (RKSS SID, RKSS STAR,
  RKSS RNP RWY 14L...), built from the city's airports and runway ends in use; several can be on (one label placement
  over all of them). This also settles the chart side of overlapping procedures; the flight side keeps only the hold
  altitude separation (same-class airports with overlapping hold areas: one goes 1,000 ft higher).
  Plan A: our own checkbox list in the infoview panel (mod UI, like the airport panel sections). The user prefers the
  game's own controls: if plan A doesn't work well, plan B is a fixed number of game infomodes made at load as slots,
  their names set per city through the localization (unused ones "empty chart").
  Built (2026-10-26, plan B; plan A would have meant drawing our own legend, as the game picks each infoview panel in a
  closed switch): `IfrChartSystem` (UIUpdate) adds the "IFR chart" infoview with 32 `ChartInfomodePrefab` slots (a
  bare InfomodeBasePrefab subclass: no colour swatch, type "Chart"). While the infoview is open the list is rebuilt
  every 64 frames: airports by code; "SID" / "STAR" where the airport flies them (OperatingValues), then "RNP RWY nn"
  per runway end in use. A change goes into the localization as a new source on the active and fallback locales
  (AddSource loads only it; RemoveSource would reload every source), the one it replaces emptied. The `IfrChartCount`
  binding tells RealisticAviation.mjs, which extends the game's `InfomodeItem` to hide the slots past it. All charts start
  unticked (user): the slots are supplemental infomodes, which ToolSystem leaves off when an infoview is opened from
  the menu.
  Step 2-1 built (2026-10-26, approach charts only; STAR / SID, obstacles, airways and the rose come next):
  `IfrChartOverlaySystem` (Rendering) draws the ticked approach charts as the preview's RNP plan view, at the preview's
  sizes (1 px = 40 m, `IfrChartCanvas.kPx`): runway outline, the "T" legs (solid, merging straight into the IF), final
  with the arrowhead at the threshold, the missed approach dashed with fly-by arcs and the turn onto final at the FAF,
  distance arrows in the lines, true courses beside them, fixes with (role) / name / altitude (AMSL, rounded up to 100,
  underlined: at or above; both lines: at). Geometry from `AtcSystem.GetApproachChart` (the tower's own
  `AtcJob.GetApproachFixes`, now static, and the go-around layout with the reference radius). `IfrChartCanvas` keeps the
  strokes and runs the preview's label placer (oriented boxes, priority order) once per build; the build is redone when
  the ticks change or, checked every 60 frames, the geometry does. `WaypointNames`: CVCVC from a hash of the position
  on a 50 m grid, unique in the city, a short avoid list; given over every approach in list order, ticked or not.
  Step 2-2 built (2026-10-26, user agreed the airway model): the game's airways are a grid (AirwayHelpers.AirwayMap:
  three lanes per cell) its paths cross the map on, so an airway on the chart is the straight line from the airport to
  each airplane outside connection, designated T1, T2... by bearing from the map centre.
  - STAR chart: the airway in grey to the STAR's first fix, an open triangle on the contact ring (radius + 0.5 NM, where
    the arrival first calls and leaves the airway); from it one STAR per runway end in use to the IAF of its sector, or
    the IF from straight in (`AtcSystem.ApproachSide`, as ChooseApproachSide), named after the first fix + validity 1 +
    the end's route letter (ends by name, A, B... without I and O). A hold (right turns, reference radius, the airport's
    leg length) at each fix a STAR leads to: MAX SPD = highest `GetHoldingSpeed` over the AirplaneData prefabs in shown
    knots rounded up to 10, MNM ALT = the stack's lowest level there (class band raised over the survey floor), and the
    leg as "1.5 NM" (the mod flies distance legs, as RNAV holds; the preview's "1 MIN" was a placeholder). Lines that
    don't fit are dropped longest first, as in the preview (with 1.5 NM legs MAX SPD usually is).
  - SID chart: per runway end and airway, as SmoothDepartureTurn flies it with the reference radius: straight 460 m past
    the runway end (`AtcSystem.kSidStraight`), a turn towards the first airway point from 4 km beyond the nearest one that
    the turn can reach (FindDepartureFix / IsReachable), direct to it (open triangle, arrowhead, "KOVAN 1A"), then the
    airway on to the map edge. No climb-gradient notes yet (with the obstacles, step 2-3).
  - Code: `IfrCharts` (Symbols / Approach / Procedures), `AtcSystem.Charts.cs` (GetApproachChart, GetSid, contact
    point, outside connections, holding speed). Lines into a waypoint stop at its circle (the preview's filled symbols
    hid them). Validity number fixed at 1 for now (`IfrCharts.kValidity`); stored per procedure + API later.
  Step 2-3 built (2026-10-26): obstacles. `IfrChartObstacles` searches on a worker thread whenever a build starts (the
  chart opens, ticks or procedures change; the old chart stays up meanwhile), over the ticked charts' areas: approach =
  the controlling obstacle of each initial segment and the intermediate one (highest top + MOC there, primary full,
  secondary tapering, only one above the threshold) and up to 3 through the final approach surface (50 m under the
  glide path from 15 m at the threshold, 1,000 ft wide, 15 % splay, floored by 2 % from 60 m: worst first, terrain as
  one spot height); STAR = the controlling one of each STAR leg (initial MOC) and each hold (reach = leg + 2 r); SID =
  up to 3 per SID through the 2.5 % surface from the runway end (5 m up, 150 m each side + 15° splay, along the turn),
  numbered steepest first, with the note block "MNM CLIMB 200 FT/NM" + "MNM CLIMB x.x% UNTIL n FOR OBST (k)"
  (gradient = (top - 5 m) / d + 0.8 %, until = top + 0.8 % d, AMSL rounded up to 100) placed round the airport.
  Terrain sampled every 200 m, buildings from 10 m by prefab top. Symbols: the VFR shapes (`ObstacleSymbols.AddToCanvas`,
  spire from 1,000 ft above the ground, ^ below, foot dot, no rays), terrain a dot; elevation AMSL rounded up, thousands
  spaced; at the preview's sizes (24 / 15 px). Airways were done in 2-2; the label placer covers obstacles and notes.
  Step 3 built (2026-10-26; checked in game by the user 2026-10-27): airport panel "Approach charts" buttons (one per runway end with the
  IAP) open `ApproachChartWindowSystem` (RawValueBinding ApproachChart): header, missed approach text, TA 14 000 / FL 140
  (the preview's values), MSA (4 sectors to the airspace radius, highest top + 1,000 ft, `IfrChartObstacles` Sector), the
  SVG profile (preview port; hatching as clipped lines), minima, rate of descent, coding tables. RDH 0: the tower's glide
  path comes down to the threshold. Names shared with the map through `IfrProcedureCollector`.
  First look in game (user, 2026-10-26): text far too big (halved: `IfrCharts.kTextScale` 0.5, lines and symbols
  unchanged); STAR/SID drew only the runway: airplane outside connections were looked for as roots of airplane stops and
  none were found; now the OutsideConnection objects whose prefab's OutsideConnectionData takes Air. The dark infoview map
  makes black ink hard to read: instead of painting a background, the infoview's own m_DefaultColor / m_SecondaryColor (the colours of everything it doesn't highlight; game default 0.7 / 0.6 grey) are set to white / 0.9 (user's idea: some infoviews look bright). Line widths halved (`kLineScale` 0.5; arrowheads and symbols unchanged).
  That did nothing (reverted). Natural resources and pollution colour the terrain through TerrainRenderSystem.
  overrideOverlaymap (RGBA texture, one channel per active terrain colour entry: channel = colour index - 1; water:
  WaterRenderSystem, secondary index - 5), written by OverlayInfomodeSystem (PreCulling) and smoothed by the shader.
  Tried: a "White background" entry (gradient infomode, colour group 0 + secondary 1) and a system right after
  OverlayInfomodeSystem putting in a texture with that channel full: the terrain changed only a little and stayed dark
  (the terrain shader is lit; 06:44 game time). Now the white background is overlay strips over the whole world (white,
  alpha 0.92, 2 km wide, under the chart, in IfrChartOverlaySystem): overlays aren't lit. The legend entry is a building
  colour entry with a white swatch, as the VFR chart's. (VFR terrain tints and built-up areas through the texture would
  stay dark at night the same way.) Obstacles within 1 km on one chart merge.
  Then (user): the white overlay flickers badly (the game dithers semi-transparent overlays; the VFR chart's tints do
  the same) and white is hard on the eyes. Back to the terrain colour route (no flicker, as telecom coverage, which is
  bright at night: so the first try was not applied rather than lit): "Paper background" entry (`PaperInfomodePrefab`,
  old-paper beige 0.93/0.89/0.76, default steps), `IfrChartPaperSystem` after OverlayInfomodeSystem, and a TEST
  `IfrChartPaperCheckSystem` after TerrainRenderSystem; both log every 300 frames (remove after the test).
  Result: works, no flicker (log: colour index 1 / secondary 5, our texture still on the terrain after its update);  still a little dark: the beige pushed to 2x, then 10x (user: just right; the water may wash out to white, fine for a paper background). Test logs and the check system removed.
  Arrowheads were stepped (parallel strips of different lengths): now a fan of lines from the tip plus a line down each side.
  Holds drawn too big (user): the chart used the procedures' radius (1.43 km); the tower flies a hold with each  airplane's own radius (AtcJob.GetTurnRadius). Now the largest of those over the airplane types (`AtcSystem.  GetLargestHoldTurnRadius`). The obstacle area round a hold still uses leg + 2 x 1.43 km, as the survey that sets MNM ALT.
  Then (user): a smaller hold scales its lines, arrows and text with it (against the preview's radius 1,300 m) and keeps  every line of the limits, sized to fit (no more dropping the longest).
  Then (user: match the rest to the real paths before testing): `AtcSystem.GetChartRadii` over the airplane types:
  holds and the missed approach (the go-around pattern: upwind extension, width, downwind end, its arcs) with the widest
  type's own radius (AtcJob.GetTurnRadius), as the tower lays the pattern out per airplane; the SID's fix chosen with the
  circle at top speed (SmoothDepartureTurn's plan radius) and its turn drawn at the lowest flying speed, both at the SID
  turn rate (min 6°/s, the airplane's own), of the type with the widest plan. Fixes and the hold obstacle areas keep the
  reference radius (the tower's fixes and the survey use it).
  Then (user): only the holds small looked odd: the whole chart now scales with them. `IfrChartCanvas.Px` (metres per  preview pixel, 40 at most) = 40 x min(1, hold radius / 1,300 m), set per build; every size in IfrCharts is a property  of it (lines, arrows, symbols, text, gaps); the hold no longer scales on its own. Zoom in to read.
  Missed approach hold (user: there was one more hold after a go-around): not cleared by the end of downwind, the tower  extends downwind 2 NM (kDownwindExtension) and holds there at pattern height, downwind inbound (EndCircuit). Drawn on  the approach chart: the dashed extension, a dashed hold (MAX SPD / MNM ALT pattern / leg) and its fix (altitude at);  window: "If not cleared, continue to X and hold.", coding table rows TF X and HM X (inbound course, leg, R).
  Clouds (user: veil the charts, no game option): `ChartCloudsSystem` keeps a global HDRP volume (priority 1000,  VolumetricClouds off, VisualEnvironment cloud type 0), enabled only while the VFR or IFR chart is open. References  Unity.RenderPipelines.Core/HighDefinition.Runtime added. Water at 10x paper turned bright blue/purple (deep water):  the water is its own entry now (`PaperInfomodePrefab.m_Water`, colour group 1, 2x, hidden in the legend, painted  while the paper entry is ticked); the ground entry is group 0 only.
  Then (user): the water went dark by day with its own 2x, and the blue shading was the clouds' / shadows' doing:  water back on the one entry at 10x (secondary group 1). Sun shadows off too while a chart is open (as the game's  shadow setting: the SunLight's HDAdditionalLightData.EnableShadows; restored on closing). Entry renamed "Background"  / "배경", colour back to white (10x).
  VFR chart the same way (user): its elevation tints and built-up areas are terrain colour entries now  (`TerrainTintInfomodePrefab`: group 0, true colours in the legend, 10x to the shader through GetColors); the  tints a gradient of the chart's tints (green, yellow, brown) in 8 steps, each cell's band written as the middle of its  step (water 0); built-up yellow with the low colour's alpha 0. `VfrChartTintSystem` (after OverlayInfomodeSystem)  writes a 448x448 texture (32 m cells, the playable area) when a sampling / count or a channel changes. The overlay  rectangles (and RectMerger) are gone.
  Then (user): ground should be green (the chart's pale green came out cyan under the bluish night light), built-up  areas brighter, 12-14 steps: 13 bands (`TerrainChartMap.kBands`, at least 100 ft each), gradient green #6fb84f /  yellow #f3e096 / brown #a87438, built-up yellow at 20x (`m_Brightness`).
  Correction (user): "12-14" meant the built-up brightness, not the bands: back to 8 bands (200 ft), the chart's yellow  #f4de9b and brown #b98b45 with the greener low end #6fb84f, built-up yellow at 13x.
  Then (user): #6fb84f far too dark; the low end is now the green of the user's sectional sample, #c8d6ba.
  Built-up yellow from the user's sample too: #f5f566 (was 1 / 0.85 / 0.25), still 13x.
  Then (user, option 2 of three offered): the legend's eight tints exactly, in three terrain colour entries (all four
  ground channels with the built-up areas): sea level-1,000 #deeac5, 1,000-2,000 #d0e1b1 | 2,000-3,000 #fdf7dc,
  3,000-5,000 #f8ebb9, 5,000-7,000 #f4d58b | 7,000-9,000 #e3bd9a, 9,000-12,000 #f1b47d, 12,000+ #b78e3f (sampled from
  the user's legend). Each entry gives the shader transparent / its first tint / its last (`m_TintGroup`, 255 steps):
  cells write 0 outside the group, 128 first band, 255 last, 191 a middle band (halfway between the two: close for
  3,000-5,000, weakest for 9,000-12,000). The legend shows the first entry (whole range); the other two are hidden in
  the UI and follow it. Pale tints look blue-grey under the game's night light (user screenshot, 2026-10-26).
  Then (user): far too bright by day: 3 steps down, tints 10x -> 7x, built-up 13x -> 10x; the IFR background stays 10x  (white either way, and lower turns the sea blue).
  Then (user: night too dark, day washed out): while a chart is open the light is fixed as the game's "day/night visuals"  off fixes it (PlanetarySystem.overrideTime, 14:30 on day 177; ChartCloudsSystem), both charts; restored on closing.  The built-up yellow already matches the user's Dallas sample (#f4f866); its highlighter look comes from the rendering.  Snow (colossal_SnowScale, a global vector SnowSystem sets) could be zeroed while a chart is open: offered, not built.
  Then (user): no fixed daylight; snow off first (colossal_SnowScale held at zero every frame while a chart is open,  restored on closing); fixed daylight only if that isn't enough.
  Then (user: snow still there): colossal_SnowScale x is what the game's own "snow visuals" setting zeroes (8 / 0), so  the ground snow is off; the white specks were falling snow: the "PrecipitationVFX" game object (ClimateRenderSystem)  is deactivated while a chart is open and restored on closing.
  Then (user: the snow texture still there, turning colours cyan / fluorescent): the scale was set to all zeros; the  game's own off value is (0, 1, 1, 1), now used. Water (user: light blue as on the chart): a VFR legend entry "Water"  / "수면" (TerrainTintInfomodePrefab.m_Water, colour group 1, #c4d6e8 from the user's sample, 2x), written into the  water's overlay texture (WaterRenderSystem.overrideOverlaymap, channel = colour index - 5).
  Then (user: the snow texture flickered and stayed): the snow system's value (set whenever the simulation runs) and ours  took turns; the scale is now also set right before each frame renders (RenderPipelineManager.beginContextRendering).
  Then (user: snow gone, but the colours still don't look like the chart's; match what shows, not the hex codes):  the daylight fix is back (snow off alone wasn't enough, as agreed). Next: calibrate each colour from a screenshot under  that light (screen colour vs the chart's, per channel in linear light, corrected input = input x target / shown).
  Under the fixed daylight 7x / 10x / 2x washed everything out (user): all three back to 1x (true colours) as the  calibration start.
  First calibration (screenshots under the fixed daylight, sampled with WPF's WebP decoder): built-up shown #d8d87c for
  #f4f866, low ground #d8d8b0 for #d7e5bb, water #e8f0e8 for #c4d6e8; gains in linear light (TerrainTintInfomodePrefab.
  m_Gain, applied through color.linear / .gamma): built-up 1.32 / 1.37 / 0.66, tints 0.99 / 1.14 / 1.15, water 0.68 /
  0.77 / 1. Pale stripe between tint groups (user): bilinear filtering let a group's channel fall from its last tint
  through its first (the cream); now point filtering on a 3x finer texture (1344², ~10.7 m texels), each texel's band
  from the interpolated height and the built-up flag interpolated and cut at one half, so contours stay smooth.
  Second calibration: built-up #e4e464, islands #d4dcbc, water #e4ecec (the water saturates towards white): gains  built-up 1.54 / 1.66 / 0.69, tints 1.02 / 1.25 / 1.14, water 0.48 / 0.62 / 0.96.
  Mountains compared (user asked me to): with the low ground's gain the warm bands showed olive / khaki (#d8c858 for
  #f4d58b, #d8c888 for #e3bd9a; the game also blends the grassy terrain texture in): gains per tint group now, low greens
  1.02 / 1.25 / 1.14, 2,000-7,000 1.23 / 1.37 / 1.63, 7,000+ 1.30 / 1.25 / 1.70 (estimates; to check on screen).
  Hairlines (user: the chart has thin black lines at the built-up edge and the shoreline): `ContourLines` (marching
  squares) traces the built-up flags at one half (the yellow's own edge; y = the cells' average building base) and the
  water depth at 0.5 m (sampled per cell in TerrainChartMap's job; y = the cell's ground), drawn as 8 m black lines;
  the terrain is sampled whenever the water entry is on too.
  Then (user screenshots): lines 8 m -> 2.5 m; the built-up outline ran over water beside the shoreline (crossings,  double lines): segments whose middle is on water (TerrainChartMap.IsWater, interpolated depth) are dropped, so the  shoreline alone is the edge there; the built-up field is smoothed (two 3x3 box blurs) before the fill and the outline  are cut at one half, so the edge is round instead of stepped at 45°.
  Then (user): the built-up outline crossed airports (their buildings are not counted, but the window around reached over them). The airports' lots (the airport building's and each upgrade's, BuildingUtils.CalculateCorners) are cut out of the smoothed field (each cell keeps at most its distance outside the lot in cells + 0.5), so the yellow and its outline go round an airport along its lot edges. Only the drawing: IsUrban (the 299 ft obstacle rule) is unchanged.
  Then (user, 2026-10-27): the game's tonemapping pressed the bright tints toward white and took their colour (measured: red 0.64 in -> 0.64 out, 1.41 in -> 0.74 out), so no per-channel gain could match the paper chart; the charts' volume now also turns the tonemapping off (Tonemapping.mode None) and the gains start again from one, to be calibrated from new screenshots. Shoreline pieces inside rivers: water from 0.1 m deep instead of 0.5 m (rivers are mostly shallower; only their pools counted). The white stripe at group edges and along the shore remained (the terrain shader filters the overlay texture whatever its filter mode, so two groups faded out together): each tint channel is spread 2 texels into the empty texels round it, so one group's fade lies under the other's colour. Open: the eight tints need six exact colours (two per channel) and two blends; only 3,000-5,000 blends well (2,000-7,000 ends, dE 2.9); the second blend is a green (dE about 6) or the 7,000-9,000 peach (green + orange, dE 6.6), the user to choose.
  Then (user): an entry per band (each with its own colour, as the water) is not possible: the ground has four colour channels, one is the built-up areas'. The greens stay exact (user: the browns are the alike ones, not the greens); the bands are paired into channels from every pairing instead of in runs: (sea level-1,000, 9,000-12,000) with 7,000-9,000 blended, (1,000-2,000, 12,000+), (2,000-3,000, 5,000-7,000) with 3,000-5,000 blended (TerrainChartMap kBandGroup / kBandValue). Watch for: a channel fading out from its full tint passes its half tint (where the other group's colour doesn't cover it, a thin fringe of that colour).
  Then (user screenshot at gain one, tonemapping off): most of the chart clipped white (water in every channel, the ground's red). With the tonemapping off the screen is the tint times the light, so one gain serves every ground colour (kGroundGain, also the built-up yellow's): from the unclipped channels of the flat low ground, green 1.25 and blue 1.0 times the tint, red taken as 1.5 -> gains (0.67, 0.80, 1.0); water 0.5. Next screenshot (unclipped) refines all three channels.
  Then (user screenshot): the lower gains came out brighter (low ground green clipped, was 0.97): the automatic exposure follows the view. The charts' volume now fixes the exposure from the sun (EV100 = log2(sun lux / (pi x 1.2)) + kExposureBias, every update while a chart is open, logged as "Chart exposure"), so a white surface in the sun alone shows white; the ground gain is back to one, water 0.5. Known issue, left for a later update (user): the white stripe at tint group edges is still there (the 2-texel channel spread didn't remove it).
  Then (user, paper chart sample): band-edge hairlines in the tint's own colour, a little darker, were tried (each band floor traced with ContourLines, drawn on the overlay in the tint x 0.8) and removed (user: they didn't sit on the colour edges and their colour was off; the overlay draws its colours unlit). Streams narrower than the 32 m cells break the shoreline into loops; later (user): draw small streams as on the chart, a single blue line along the stream instead of an outline.
  Then (user screenshot, exposure fixed, gains one): the flat low ground and the built-up yellow showed (0.84, 0.73, 0.45) times their tint, the water at 0.5 (1.96, 1.80, 1.40) times its blue -> kGroundGain (1.19, 1.37, 2.22) for every ground colour, kWaterGain (0.51, 0.56, 0.71).
  Then (user): a pale ring round the brown peaks. Where a group's full tint (255) meets another group's band, the shader's filtering takes that channel down through its half tint (128): brown fades through the lower green, the 5,000-7,000 yellow through the 2,000-3,000 cream (the white stripe). A search of every channel layout for one where the channel on top always has its half tint at the edges found none without blending the orange (dE 12), so the layout stays and the tint texture is finer instead: 6 texels a cell (2,688², ring about 5 m), written into a managed array and copied in at once (heights and bands copied to managed arrays too). The texture channels are logged ("VFR chart texture channels") to learn which channel the shader draws on top.
  Then (user screenshot; the brief pause on opening is fine, user): low ground #e4e6ce for #deeac5, open built-up ground #ede2ab for #f5f566 (its blue three times the tint's: the game keeps some of the ground's own colour under a tint), water #c1d8fb for #c4d6e8. Each step gain x tint / shown, per entry: kGroundGain (1.12, 1.43, 2.00), kUrbanGain (1.27, 1.62, 0.71), kWaterGain (0.53, 0.55, 0.59).
  Then (user screenshot): low ground #dae3c6, built-up #eee18b, water #c7d6dc. Gains (damped where two steps disagreed, the spots differ in light): kGroundGain (1.15, 1.49, 2.00), kUrbanGain (1.37, 2.00, 0.37: its blue barely follows the gain, the ground's own colour under it), kWaterGain (0.52, 0.55, 0.64).
  Then (user: every calibration step cost a game restart): ChartGainFile reads the gains from ModsData/RealisticAviation/VfrChartGains.txt (lines "ground|urban|water r g b") whenever the game takes an entry's colours (the chart opening), again only once the file changed; the entries carry m_GainKey. Calibrate by editing the file and reopening the chart; when done, move the values into the code and remove the file reading.
  Then (user): streams as on the chart. StreamLines: the water sampled again on an 8 m grid in the terrain job; the wide water is where a disc 40 m across fits (chamfer distance from the land, then within 20 m of those cells), the rest narrow; the narrow water thinned to one cell (Zhang-Suen) in the job; on collecting, the centre cells traced into lines (diagonal steps only where no side cell joins them, so stair steps aren't junctions; spurs and pieces under 4 cells dropped; two 1-2-1 smoothing passes) and drawn in #4a7fc0 (gain key "stream" in the calibration file), 8 m wide (user: as thick as the first outlines), with the water entry. Shoreline pieces with stream water within 3 fine cells and no wide water are left out.
  Then (user: the creek passed; ponds and narrow inlets came out as lines, and the shoreline cut across the inlets): each piece of narrow water (8-joined) is measured for its mean water speed (WaterUtils.SampleVelocity) in the job; slower than stream-speed (0.5, calibration file; pieces over 20 cells logged as "VFR chart narrow water") it is still water and classed wide (outlined). The shoreline is traced on the 8 m grid (the fine depths), so it follows inlets and ponds; pieces with stream water within 2 fine cells and no wide water are left out.
  Then (user: the chart stalls the game on opening, a 14900K): the terrain job was one long job registered as the terrain and water systems' reader, so they waited for it every frame, and the tracing and the 2,688² texture ran on the main thread. Now SampleRowsJob (IJobParallelFor, a row a job, on every worker) samples the cell heights and depths and the fine depths and speeds and is the only reader; ProcessJob (bands, MEF, peaks, stream classes, flow test, centre cells, fine shoreline via ContourLines' native overload) runs after it holding nothing and is not in the system's dependency; the main thread keeps only the line walking and heights. VfrChartTintSystem writes the texture on a Task and copies it in when done. Logged: "VFR chart lines" (segment counts). Colours (user screenshot after reopening, file read): every entry about 0.85 of its tint in one shot, so the hues hold and only the overall brightness moved between screenshots (the exposure log shows the same EV 14.83); ground green raised to 1.74 in the file.
  Then (user screenshot, the stall gone): water #c3d7e7 for #c4d6e8 and built-up #f7f670 for #f5f566 match; the low ground is a little grey (#dce0c8 for #deeac5); the mountains came out lemon (#fffd9b, #fcfd85): one gain does not fit every tint (the game keeps some of the ground's own colour under a tint). Each exact tint now has its own key in the file (band0..band7, the group entry's first and last tint; the blended bands follow), falling back to "ground"; the warm ones start from the gains under which the mountains looked right (1.15, 1.43, 2.00).
  Then (user screenshots, per-band gains in the file): greens, water, built-up and the orange matched; the 7,000-9,000 band, a blend of the lower green and the orange, took the green's high gain and came out lemon. Regrouped in order (user chose it from swatches of three layouts): (sea level-1,000, 1,000-2,000), (2,000-3,000, 3,000-5,000 blended, 5,000-7,000), (7,000-9,000, 9,000-12,000 blended, 12,000+); file keys band0, band1, band2, band4, band5, band7. Each group's lower band sits at half, so where the next group's channel lies on top its edge fades out cleanly.
  Done (user, 2026-10-27: "VFR 차트는 여기까지"): the file's gains moved into the code (kTintGains per band, kUrbanGain, kWaterGain; TerrainTintInfomodePrefab m_FirstGain / m_LastGain), ChartGainFile and the file removed, the calibration logs dropped. Stream speed: still pieces measured 0 to 0.28, streams 2.9 up -> kMinStreamSpeed 1. Fixed: a flowing piece was put back to narrow right after its flood and so flooded again from each of its cells (thousands of log lines, quadratic time); its cells now keep a measured mark until every piece is done.
  Then (user: the IFR chart hurts the eyes): its background was white x 10, pressed down by the tonemapping before; with the tonemapping off it shows pure white. Now the paper is #f0f0ec on screen (user chose it over a calmer grey), reached through a gain in linear light like the VFR colours, and the water has its own entry (RealisticAviationIfrPaperWater, colour group 1, hidden in the legend like the VFR tint groups 2 and 3, on with "Background") for its own gain. ChartGainFile is back for the calibration (ModsData/RealisticAviation/ChartGains.txt, keys ifr-paper and ifr-paper-water; start 1.2/1.5/2.0 and 0.5/0.5/0.55); remove it once the values are in.
  Then (user screenshots): the IFR background came out at once without glare, a warm paper colour (water #feeed4, ground #fefddd) rather than #f0f0ec; the user kept it, and the VFR colours after the move into the code as before. The starting gains stay in the code; ChartGainFile and its file removed again.
- VOR (user, 2026-10-26): a per-airport "VOR" checkbox in the airport panel, saved like line up and wait, default on for
  class B and C, off for D (a small field is less likely to have one; the class doesn't decide it). On: VOR symbol, info
  box (airport name, frequency in 108.00-117.95 from the airport position, 3-letter ident from the ICAO code with Morse)
  and the compass rose round it, as on the AIP. The procedures are RNP either way. Add-on API too.
  Built (2026-10-27) from docs/vor-preview.html (user: symbol 1-a, the ICAO VOR hexagon with a dot; info box 2-a, the
  AIP's "NAME VOR" / "113.60 PLD" / Morse; rose 1 NM, as 2 NM was too wide for a 7.7 NM map; at the airport's centre, on
  every SID / STAR / approach layer of the airport, not on the VFR chart). `VorSetting` (enabled + class, default by
  class as `LineUpSetting`), `AirportSettings.IsVorEnabled / SetVor`, airport panel checkbox "VOR", API 5
  `Api.Airports.SetVor` and `AirportInfo.Vor` (and `SetLongPushback` / `AirportInfo.LongPushback`). `VorStation`: the
  frequency is one of the 160 VOR channels (108.00-111.95 with even tenths, 112.00-117.95, 50 kHz) picked by a hash of the
  position rounded to 50 m; the ident the last three letters of the airport's code ("VOR" without three); the name the
  airport's name in the chart font's letters and digits. `IfrCharts.Vor` draws the rose (ticks 10 deg, longer every 30,
  0 9 18 27, north arrowhead), the hexagon and the box (placed by the label placer, lower right first); the collector
  adds the VOR to the signature, so toggling it redraws the chart. Tested (user, 2026-10-27: shows right, size fine).
- Hold step-down call (user report: "descend" said while the airplane climbs): the words now follow the airplane's height
  when said (`AtcMessage.m_Altitude`): below the new level -> "climb and maintain", above -> "descend and maintain",
  within 200 ft -> "maintain". An arrival comes in on the airway (~1,000 m above the ground), under the stack, and was
  still climbing to its old level when told to step down. Tested (user, 2026-10-27: "climb" heard).
- Backlog (user, 2026-10-26: later): limit the stack height. Departures from the city's own airports to a saturated one
  could wait at the gate (the tower already runs their pushback); airplanes from outside connections spawn at the map edge
  through the game's own systems, so stopping them there would need game internals. Overlapping holds of nearby airports:
  recommended one shared stack (levels taken jointly, 1,000 ft apart) instead of odd/even thousands, which doubles the height.
- License (user, 2026-10-27, after seeing an unofficial re-upload of a popular mod): no license from the next update on;
  LICENSE (MIT) and the README's license section removed, nothing put in their place (user). The page description and
  the GitHub link stay as they are. Versions already released under MIT stay so.
- Radio exchange click (user, 2026-10-27): clicking an exchange in any radio box (airport or airplane panel) selects its
  airplane and follows it with the game's own functions (SelectedInfoUISystem.SetSelection, then Focus = the info panel's
  follow button; user: use the game's functions, a custom camera move was rejected); a broadcast ("attention all
  aircraft") selects and follows the airport that spoke (user: consistent UI); an airplane that has left does nothing. Under the
  pointer an exchange lights up (a white wash, the newest one's blue a step brighter). RadioBox writes each exchange's
  airplane and airport (index, version); triggers "SelectAirplane" / "SelectAirport" in AirplaneAtcSection. Works in game (user), except:
  Known issue (user, 2026-10-27: leave it): an airplane holding on cargo airway 1 (outside the map): the camera starts to
  follow and stops short. The game's own click and follow button on the same airplane work, so it is not the camera's
  map limit; the difference between SetSelection + Focus from the radio and the game's own path is not found yet.
- Long pushback (user, 2026-10-27; "긴 푸쉬백"): a checkbox per airport in the airport panel (shown while pushback is
  on), saved as the tag `LongPushbackAirport` on the airport building. Airplanes there push back about their own length:
  the push and its limits x2 (`kLongPushbackFactor`: 30-80 m instead of 15-40 m). An add-on's fixed distance for the
  airport is used as set (user). The detailed tuning stays with add-ons (1.2.2 decision); this is the one common choice.
  Known risk: a push over 40 m may reach an arrival waiting at kGateHoldDistance (100 m) before the stand. Tested (user, 2026-10-27): pushes of 37-58 m (40 m at most before). A push still ends where the path behind turns away (kPushbackTurnDot), so short straight segments behind a gate keep it shorter than the airplane's length; kept so (user).
- Procedure designators (user, 2026-10-26), ICAO Annex 11 App. 3: basic indicator (SID: its last fix, STAR: its first
  fix) + validity number + route letter. Route letter per runway end in use, A, B, C... within the airport, skipping I
  and O. Validity number 1 for now, but kept as data (per airport, per procedure; saved with the airport like its other
  settings) and settable through the API, for a later procedure editor (real procedures count it up 1-9 when revised).
- Coding tables (user: "do it as the AIP"): one table per transition and one for the missed approach, each titled with
  its procedure above it ("RNP RWY 07 MODEK TRANSITION", as the AIP's "RNAV BULTI 1U"), the AIP's columns: serial
  number, path descriptor (IF first, then TF; the missed approach starts after the MAPt, so TF from its first row),
  waypoint identifier, fly-over (the MAPt RW07: Y), course/track °T, distance, turn direction (the turn onto that leg),
  altitude (+ at or above, - at or below, @ at), speed, VPA/RDH (final: -5.0°/50), navigation specification (RNP APCH).
  No coordinates column: the game has no latitude/longitude. The preview's blue obstacle-area guide lines are removed
  (they were never meant for the game).
- Hold MAX SPD (user: the mod's real value): the tower flies each airplane in a hold at its own speed
  (`GetHoldingSpeed`: max(1.15 x minimum flying speed, the level-attitude speed), capped at its maximum). The chart's
  "MAX SPD n KT IAS" is the highest of those over the airplane types in the city, in shown units (internal m/s / 2 x 1.944,
  see the speed-units note), rounded up to 10 kt; worked out from the prefabs when the chart is built. The preview's 230
  is a placeholder (no prefab data outside the game).
