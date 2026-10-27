using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Edge = Game.Net.Edge;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    public partial class AtcSystem
    {
        // ---------------------------------------------------------------- tower job

        private partial struct AtcJob : IJob
        {
            // Each airplane in a holding stack flies 1,000 ft above the one below it, as in real holding stacks.
            private const float kStackSpacing = 1000f * 0.3048f;
            private const byte kNoStackLevel = byte.MaxValue;

            // An airplane acts on a clearance this many frames (one simulation second) after it is given, the time of the readback.
            private const uint kResponseFrames = 60;

            // Check-in altitudes: feet, and the vertical speed (m/s, about 300 ft/min) beyond which the pilot reports climbing
            // or descending instead of level.
            private const float kMetersPerFoot = 0.3048f;
            private const float kLevelClimbRate = 1.5f;

            // A departure checks in with departure control (and may call the next airport) once this high above its field
            // (1,000 ft), or this many frames (20 simulation seconds) after takeoff if it stays lower.
            private const float kCheckInHeight = 1000f * kMetersPerFoot;
            private const uint kCheckInTimeoutFrames = 60 * 20;

            // A departure whose wheels are rolling (faster than this, m/s, about 5 km/h) is on its takeoff roll and is not
            // stopped for a closure (there are no V1/VR/V2 speeds to judge it by).
            private const float kRollingSpeed = 1.4f;

            // Last resort only: the mod's taxi holds give up after this many frames behind the same airplane (three simulation
            // minutes), when a deadlock clearly won't clear by itself; the airplanes may then overlap as in the base game.
            private const uint kTaxiHoldTimeout = 60 * 60 * 3;

            // "Extend downwind" lasts at most this far beyond the normal end of downwind before the airplane is told to hold.
            internal const float kDownwindExtension = 2f * Setting.kMetersPerNauticalMile;

            // An arrival whose gate is occupied waits with its nose this far before the stand lane: clear of the pushback
            // area (a push of at most kMaxPushbackDistance plus half the airplane and kPushbackClearance).
            private const float kGateHoldDistance = 100f;
            private const float kGateLookAhead = 2000f;

            // Airplanes caught in a wait-for cycle ignore all taxi holds and blockers for this many frames (half a simulation
            // minute), enough to drive past each other; cycles are followed at most this many links.
            private const uint kDeadlockReleaseFrames = 60 * 30;

            // Two departures this close (metres of path to the runway) stand at the same spot: the earlier request goes first.
            private const float kSameSpotDistance = 5f;
            private const int kMaxDeadlockChain = 8;

            // The inner surface area of class B and C zones (as drawn by the airspace infoview), as a share of the radius.
            private const float kInnerZoneFactor = 0.5f;

            // An airplane leaves a zone only this far beyond its edge, though it enters at the edge itself: without the margin one
            // skimming the boundary flipped between "radar contact" and "contact center" every few seconds.
            private const float kZoneExitMargin = 0.5f * Setting.kMetersPerNauticalMile;

            // A clearance is only taken back from a taxiing airplane that can still stop short (speed slack in m/s).
            private const float kHoldShortSpeedSlack = 0.5f;
            private const int kMaxStackLevel = 63;

            // Leaving a hold: hand back once within this angle (cosine) of the path point and at least this many turn
            // radii from it; until then, line up through a gate this many turn radii behind the point.
            private const float kHandBackAlignment = 0.866f;
            private const float kHandBackRadiusFactor = 1.5f;
            private const float kGateRadiusFactor = 3f;

            // Holding speed as a factor of the airplane's minimum flying speed.
            private const float kHoldingSpeedFactor = 1.15f;
            // Extra share of the speed range above the speed at which the nose would be level.
            private const float kLevelAttitudeMargin = 0.05f;

            // Path elements scanned beyond the navigation lanes when looking for the next runway, and past its entry for its first runway lane.
            private const int kMaxRunwayLaneScan = 8;
            private const int kMaxPathScan = 96;

            // A waiting airplane looks this much further than its request distance, so it keeps its place in line.
            private const float kKeepRequestFactor = 1.5f;

            // Ground airplanes ask for the runway at least this far before it, plus their stopping distance.
            private const float kMinGroundRequestDistance = 200f;

            // A ground airplane gets its takeoff or crossing clearance only within this far (metres) of its hold-short point.
            private const float kGroundClearanceMargin = 100f;

            // How far along its path an airborne airplane looks for its runway (well beyond the largest zone).
            private const float kAirScanDistance = 20000f;
            private const float kGroundRequestMargin = 50f;

            // Traffic pattern: 1,500 ft above the runway (FAA pattern altitude for large and turbine airplanes); upwind runs at
            // least this far past the runway end; legs turn this many turn radii before each corner. (Parallel runways: see
            // RunwayGeometry.IsParallelOnSide.) The landing decision is made at the decision height above
            // the runway (within 2 NM of the threshold): an arrival goes around there if the runway is not clear.
            internal const float kPatternHeight = 1500f * 0.3048f;

            /// <summary>Steepest climb or descent the tower steers an airplane on (holding, pattern, to the final approach fix): tan 6 degrees.</summary>
            private const float kSteeringSlope = 0.1051f;
            internal const float kUpwindExtension = 0.5f * Setting.kMetersPerNauticalMile;
            private const float kCircuitLeadFactor = 1.5f;
            private const float kDecisionDistance = 2f * Setting.kMetersPerNauticalMile;

            // Response-ratio scheduling: expected runway time of each kind of use, in game seconds.
            private const float kSecondsPerFrame = 1f / 60f;
            private const float kLandingRunwaySeconds = 30f;
            private const float kTakeoffRunwaySeconds = 30f;

            /// <summary>A takeoff clearance says "no delay" when the next arrival is due within the departure gap plus this.</summary>
            private const float kNoDelayMarginSeconds = 30f;

            // In-trail spacing: extra seconds per size class when a larger airplane lands ahead of a smaller one (wake turbulence).
            private const float kWakeSecondsPerClass = 20f;
            private const float kTaxiSpeedEstimate = 10f;
            private const float kMinServiceSeconds = 10f;

            // Line up and wait: the landing airplane must be this far past the entry point, and the lined-up airplane
            // stops with its tail this far inside the runway lane.
            private const float kLineUpBehindDistance = 150f;
            private const float kLineUpInset = 10f;

            // Pushback: starts below kPushbackStartSpeed when the nose points against the path (cosine below kPushbackStartDot).
            // Pushes at kPushbackSpeed for half the airplane's length (kMin..kMaxPushbackDistance), or less when the path turns
            // away (cosine below kPushbackTurnDot after kPushbackSettleDistance), then swings at kPushbackTurnRate degrees/s.
            // Waits up to kPushbackUnknownPathTicks ticks for a path. Taxiing airplanes keep kPushbackClearance from the
            // pushing airplane, checking their path every kPushbackSampleStep metres.
            private const float kPushbackStartSpeed = 1f;
            private const float kPushbackStartDot = -0.5f;
            private const float kPushbackTurnDot = 0.866f;
            private const float kPushbackSpeed = 3f;
            private const float kPushbackLengthFactor = 0.5f;
            private const float kMinPushbackDistance = 15f;
            private const float kMaxPushbackDistance = 40f;

            // "Long pushback" at an airport (its checkbox): the push and its limits this many times as long (about the
            // airplane's own length, 30-80 m; user). An add-on's fixed distance for the airport is used as set.
            private const float kLongPushbackFactor = 2f;
            private const float kPushbackSettleDistance = 5f;
            private const float kPushbackTurnRate = 15f;
            private const int kPushbackUnknownPathTicks = 30;
            private const float kPushbackClearance = 10f;
            private const float kPushbackSampleStep = 10f;

            // Ground control holds a pushback while an airplane moves within this margin of its area, for at most this long
            // (game seconds); after the turn the airplane stands this long while its engines start and the tug disconnects.
            private const float kPushbackApprovalMargin = 40f;
            private const float kPushbackApprovalTimeout = 60f;
            private const float kEngineStartSeconds = 10f;

            // A runway closes while a storm, hail or tornado comes within this distance of it beyond the phenomenon radius (1 NM).
            private const float kClosureMargin = Setting.kMetersPerNauticalMile;

            // Taxi spacing looks this much further than the gap plus the stopping distance.
            private const float kTaxiScanMargin = 20f;

            // A junction with a crossing or merging taxiway is taken up while an airplane's centre line is this close (metres).
            private const float kCrossingClearance = 20f;

            // Two airplanes this close to a junction by distance (metres) reach it together: the lower entity index goes first.
            private const float kCrossingTie = 2f;

            // A ground airplane that was cleared but hasn't reached the runway in this many frames loses its turn
            // and goes to the back of the line, so one stuck airplane can't block a runway forever.
            private const uint kGroundClearanceTimeout = 60 * 60 * 3;

            // The same safety net for arrivals; a cleared arrival normally reaches the runway well within this.
            private const uint kAirClearanceTimeout = 60 * 60 * 5;

            // Radio: the airport radio log keeps this many transmissions; an airplane's latest one is forgotten after this many
            // frames without a new one (about an in-game hour).
            private const int kRadioLogLength = kRadioLogCapacity;

            /// <summary>Added to the response ratio of an airplane an add-on gave priority: ahead of everyone else.</summary>
            private const float kPriorityRatio = 1000000f;
            private const uint kMessageLifetimeFrames = 60 * 60 * 3;

            // Diagnostics: every this many ticks, log airplanes that have waited longer than this many frames.
            private const uint kDiagnosticInterval = 64;
            private const uint kLongWaitFrames = 60 * 60;

            [ReadOnly] public ComponentLookup<Owner> m_OwnerData;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;
            [ReadOnly] public ComponentLookup<Game.Net.CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<Game.Net.ConnectionLane> m_ConnectionLaneData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<AircraftData> m_PrefabAircraftData;
            [ReadOnly] public ComponentLookup<AirplaneData> m_PrefabAirplaneData;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> m_PrefabGeometryData;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;
            [ReadOnly] public ComponentLookup<Moving> m_MovingData;
            public ComponentLookup<PathOwner> m_PathOwnerData;
            public ComponentLookup<AircraftCurrentLane> m_CurrentLaneData;
            public ComponentLookup<AircraftNavigation> m_NavigationData;
            [NativeDisableParallelForRestriction] public BufferLookup<AircraftNavigationLane> m_NavigationLanes;
            [ReadOnly] public BufferLookup<PathElement> m_PathElements;
            [ReadOnly] public BufferLookup<LaneObject> m_LaneObjects;
            [ReadOnly] public BufferLookup<LaneOverlap> m_LaneOverlaps;
            [ReadOnly] public ComponentLookup<LineUpSetting> m_LineUpData;
            [ReadOnly] public ComponentLookup<LongPushbackAirport> m_LongPushbackData;
            [ReadOnly] public ComponentLookup<RunwayPattern> m_RunwayPatternData;
            [ReadOnly] public ComponentLookup<Game.Events.WeatherPhenomenon> m_WeatherPhenomenonData;
            [ReadOnly] public ComponentLookup<Game.Events.Duration> m_DurationData;
            [ReadOnly] public ComponentLookup<Game.Events.InDanger> m_InDangerData;
            [ReadOnly] public ComponentLookup<Blocker> m_BlockerData;
            [ReadOnly] public ComponentLookup<GroundDelaySetting> m_GroundDelayData;
            [ReadOnly] public ComponentLookup<Game.Common.Target> m_TargetData;
            [ReadOnly] public ComponentLookup<Game.Routes.Connected> m_ConnectedData;
            [ReadOnly] public ComponentLookup<Game.Routes.BoardingVehicle> m_BoardingVehicleData;
            [ReadOnly] public ComponentLookup<Game.Vehicles.CargoTransport> m_CargoTransportData;
            [ReadOnly] public ComponentLookup<Game.Vehicles.PublicTransport> m_PublicTransportData;
            [ReadOnly] public ComponentLookup<Stopped> m_StoppedData;
            [ReadOnly] public ComponentLookup<Game.Routes.RouteLane> m_RouteLaneData;
            [ReadOnly] public ComponentLookup<Taxiway> m_TaxiwayData;

            [ReadOnly] public NativeList<Entity> m_Airplanes;
            [ReadOnly] public NativeList<Entity> m_WeatherEvents;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity> m_RunwayOf;
            public NativeParallelHashMap<Entity, RunwayState> m_Runways;
            [ReadOnly] public NativeParallelHashMap<Entity, RunwayGeometry> m_RunwayGeometry;
            public NativeParallelHashMap<Entity, Flight> m_Flights;
            public NativeList<Entity> m_HoldingFlights;
            public NativeParallelHashMap<Entity, Pushback> m_Pushbacks;
            // Airplanes standing at their gate, with the last tick they were seen there: only these are pushed back.
            public NativeParallelHashMap<Entity, uint> m_AtGate;
            public NativeParallelHashMap<Entity, DepartureTurn> m_DepartureTurns;
            public NativeReference<uint> m_Sequence;
            [ReadOnly] public NativeParallelHashMap<Entity, FixedString128Bytes> m_PrefabNames;
            [ReadOnly] public NativeParallelHashMap<Entity, int> m_GateCounts;
            [ReadOnly] public NativeParallelHashMap<Entity, AirportSpace> m_Airspaces;
            [ReadOnly] public NativeParallelHashMap<Entity, AtisState> m_AtisStates;

            // Set by add-ons through the public Api (main thread), copied in each update by AtcSystem.FillApiState.
            [ReadOnly] public NativeParallelHashMap<Entity, Entity> m_ApiClosures;
            [ReadOnly] public NativeParallelHashMap<Entity, OperatingOverride> m_ApiOverrides;
            [ReadOnly] public NativeParallelHashMap<Entity, byte> m_ApiHeld;
            [ReadOnly] public NativeParallelHashMap<Entity, byte> m_ApiPriority;
            public NativeList<ApiGoAround> m_ApiGoArounds;
            public NativeParallelHashMap<Entity, Entity> m_AirportClosures;
            /// <summary>Diagnostics: what the taxi spacing or a pushback held each ground airplane behind, last tick.</summary>
            public NativeParallelHashMap<Entity, Entity> m_HeldBehind;
            public NativeParallelHashMap<Entity, TaxiHold> m_TaxiHolds;
            public NativeParallelHashMap<Entity, FreeSpeed> m_FreeSpeeds;
            public NativeParallelHashMap<Entity, ZoneState> m_Zones;
            public NativeParallelHashMap<Entity, RunwayExit> m_RunwayExits;
            [ReadOnly] public NativeParallelHashMap<Entity, ProcedureFloors> m_ProcedureFloors;
            public NativeParallelHashMap<Entity, Entity> m_GateHolds;
            public NativeParallelHashMap<Entity, DeadlockRelease> m_Released;
            public NativeParallelHashMap<Entity, int> m_GroundCounts;
            public NativeParallelHashMap<Entity, int> m_GroundDelays;
            public NativeParallelHashMap<Entity, AtcMessage> m_LastMessages;
            public NativeList<AtcMessage> m_RadioLog;
            public NativeReference<uint> m_RadioVersion;
            [ReadOnly] public NativeParallelHashMap<Entity, HazardKinds> m_HazardKinds;

            public AtcSettings m_Settings;
            public uint m_Tick;
            public uint m_Frame;

            public void Execute()
            {
                m_HoldingFlights.Clear();
                if (m_Runways.IsEmpty)
                {
                    m_AirportClosures.Clear();
                    m_Flights.Clear();
                    m_RunwayExits.Clear();
                    return;
                }
                try
                {
                    UpdateClosures();
                    ApplyApiGoArounds();
                    NativeArray<FlightScan> scans = new NativeArray<FlightScan>(m_Airplanes.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    m_GroundCounts.Clear();
                    int touched = 0;
                    for (int i = 0; i < m_Airplanes.Length; i++)
                    {
                        FlightScan scan = Scan(m_Airplanes[i]);
                        CountGroundTraffic(scan);
                        if (UpdateFlight(ref scan))
                        {
                            touched++;
                        }
                        UpdateZone(scan);
                        scans[i] = scan;
                    }
                    if (touched != m_Flights.Count())
                    {
                        RemoveStaleFlights();
                    }
                    if (!m_RunwayExits.IsEmpty)
                    {
                        RemoveStaleRunwayExits();
                    }
                    UpdateGroundDelays();
                    GrantRunways();
                    DetectDeadlocks(scans);
                    m_HeldBehind.Clear();
                    m_GateHolds.Clear();
                    NativeList<PushbackArea> pushbackAreas = CollectPushbackAreas();
                    for (int i = 0; i < scans.Length; i++)
                    {
                        Apply(scans[i], pushbackAreas);
                        ApplyGlidePath(scans[i]);
                        SmoothDepartureTurn(scans[i]);
                    }
                    pushbackAreas.Dispose();
                    scans.Dispose();
                    StepDownHolds();
                    EndPushbackTick();
                    EndDepartureTurnTick();
                    EndTaxiHoldTick();
                    EndFreeSpeedTick();
                    EndZoneTick();
                    EndReleaseTick();
                    if (m_Tick % kDiagnosticInterval == 0)
                    {
                        PruneMessages();
                    }
                }
                catch (Exception e)
                {
                    m_HoldingFlights.Clear();
                    m_AirportClosures.Clear();
                    Mod.Log.Error(e, $"{nameof(AtcSystem)} tick failed");
                }
            }

            private Entity RunwayOfLane(Entity lane)
            {
                if (m_OwnerData.TryGetComponent(lane, out Owner owner) && m_RunwayOf.TryGetValue(owner.m_Owner, out Entity runway))
                {
                    return runway;
                }
                return Entity.Null;
            }

        }
    }
}
