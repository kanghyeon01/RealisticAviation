using System.Collections.Generic;
using Colossal.Entities;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    public partial class AtcSystem
    {
        // ------------------------------------------------------------ behind the public Api (main thread only)
        // What add-ons set is kept here in managed collections and copied into native ones for the tower job each update
        // (FillApiState), so no add-on code ever runs inside the job. Nothing here is saved with the city.

        /// <summary>How many transmissions the radio log keeps (also the tower job's limit).</summary>
        internal const int kRadioLogCapacity = 64;

        private NativeParallelHashMap<Entity, Entity> m_ApiClosures;
        private NativeParallelHashMap<Entity, OperatingOverride> m_ApiOverrides;
        private NativeParallelHashMap<Entity, byte> m_ApiHeld;
        private NativeParallelHashMap<Entity, byte> m_ApiPriority;
        private NativeList<ApiGoAround> m_ApiGoArounds;

        /// <summary>Airports and runways closed by add-ons, with the reason they gave.</summary>
        private readonly Dictionary<Entity, string> m_ClosureReasons = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, OperatingOverrides> m_Overrides = new Dictionary<Entity, OperatingOverrides>();
        private readonly HashSet<Entity> m_Held = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Priority = new HashSet<Entity>();
        private readonly List<ApiGoAround> m_PendingGoArounds = new List<ApiGoAround>();
        private bool m_ApiChanged;

        /// <summary>The operating values of the last tower update (the weather go-around chance comes from the climate).</summary>
        private AtcSettings m_LastSettings;

        private void CreateApiState()
        {
            m_ApiClosures = new NativeParallelHashMap<Entity, Entity>(4, Allocator.Persistent);
            m_ApiOverrides = new NativeParallelHashMap<Entity, OperatingOverride>(4, Allocator.Persistent);
            m_ApiHeld = new NativeParallelHashMap<Entity, byte>(4, Allocator.Persistent);
            m_ApiPriority = new NativeParallelHashMap<Entity, byte>(4, Allocator.Persistent);
            m_ApiGoArounds = new NativeList<ApiGoAround>(4, Allocator.Persistent);
        }

        private void DisposeApiState()
        {
            m_ApiClosures.Dispose();
            m_ApiOverrides.Dispose();
            m_ApiHeld.Dispose();
            m_ApiPriority.Dispose();
            m_ApiGoArounds.Dispose();
        }

        /// <summary>A new city: what add-ons set for the old one no longer applies (entities may even be reused).</summary>
        private void ResetApiState()
        {
            m_ClosureReasons.Clear();
            m_Overrides.Clear();
            m_Held.Clear();
            m_Priority.Clear();
            m_PendingGoArounds.Clear();
            m_ApiGoArounds.Clear();
            m_ApiChanged = true;
        }

        /// <summary>Copies what add-ons set into the tower job's collections; the job must be complete.</summary>
        private void FillApiState()
        {
            if (m_ApiChanged)
            {
                m_ApiChanged = false;
                m_ApiClosures.Clear();
                foreach (Entity key in m_ClosureReasons.Keys)
                {
                    // The closure's "cause" is the closed airport or runway itself; its reason text is looked up by it.
                    m_ApiClosures.TryAdd(key, key);
                }
                m_ApiOverrides.Clear();
                foreach (KeyValuePair<Entity, OperatingOverrides> pair in m_Overrides)
                {
                    m_ApiOverrides.TryAdd(pair.Key, ToJob(pair.Value));
                }
                m_ApiHeld.Clear();
                foreach (Entity entity in m_Held)
                {
                    m_ApiHeld.TryAdd(entity, 1);
                }
                m_ApiPriority.Clear();
                foreach (Entity entity in m_Priority)
                {
                    m_ApiPriority.TryAdd(entity, 1);
                }
            }
            m_ApiGoArounds.Clear();
            for (int i = 0; i < m_PendingGoArounds.Count; i++)
            {
                m_ApiGoArounds.Add(m_PendingGoArounds[i]);
            }
            m_PendingGoArounds.Clear();
        }

        private static OperatingOverride ToJob(in OperatingOverrides values)
        {
            return new OperatingOverride
            {
                m_ArrivalSpacingSeconds = values.ArrivalSpacingSeconds ?? float.NaN,
                m_DepartureGapSeconds = values.DepartureGapSeconds ?? float.NaN,
                m_FinalLength = values.FinalLengthMeters ?? float.NaN,
                m_PatternWidth = values.PatternWidthMeters ?? float.NaN,
                m_WeatherGoAroundChance = values.WeatherGoAroundChance ?? float.NaN,
                m_GlideTan = values.GlidePathDegrees.HasValue
                    ? math.tan(math.radians(math.clamp(values.GlidePathDegrees.Value, 1f, 30f)))
                    : float.NaN,
                m_GlidePath = Toggle(values.InstrumentApproach ?? values.GlidePath),
                m_StandardArrival = Toggle(values.StandardArrival),
                m_StandardDeparture = Toggle(values.StandardDeparture),
                m_ApproachHeightLimits = Toggle(values.ApproachHeightLimits),
                m_WeatherGoArounds = Toggle(values.WeatherGoArounds),
                m_Pushback = Toggle(values.Pushback),
                m_CloseForTornado = Toggle(values.CloseForTornado),
                m_CloseForHail = Toggle(values.CloseForHail),
                m_CloseForOtherDisasters = Toggle(values.CloseForOtherDisasters),
                m_LimitClosureTime = Toggle(values.LimitClosureTime),
                m_HoldingLegLength = values.HoldingLegLengthMeters.HasValue ? math.max(100f, values.HoldingLegLengthMeters.Value) : float.NaN,
                m_HoldShortSetback = values.HoldShortSetbackMeters.HasValue ? math.max(0f, values.HoldShortSetbackMeters.Value) : float.NaN,
                m_TaxiSpacing = values.TaxiSpacingMeters.HasValue ? math.max(0f, values.TaxiSpacingMeters.Value) : float.NaN,
                m_PushbackDistance = values.PushbackDistanceMeters.HasValue ? math.clamp(values.PushbackDistanceMeters.Value, 5f, 100f) : float.NaN,
                m_MaxClosureMinutes = values.MaxClosureMinutes ?? float.NaN,
            };
        }

        /// <summary>A nullable switch for the job: -1 keeps the option, 0 off, 1 on.</summary>
        private static sbyte Toggle(bool? value)
        {
            return value.HasValue ? (sbyte)(value.Value ? 1 : 0) : (sbyte)-1;
        }

        // ---- Closures

        internal void SetClosure(Entity key, string reason)
        {
            m_ClosureReasons[key] = string.IsNullOrWhiteSpace(reason) ? "operational reasons" : reason.Trim();
            m_ApiChanged = true;
        }

        internal bool ClearClosure(Entity key)
        {
            bool removed = m_ClosureReasons.Remove(key);
            m_ApiChanged |= removed;
            return removed;
        }

        /// <summary>The reason an add-on gave for a closure, looked up by what closed the airport (the airport or runway itself).</summary>
        internal bool TryGetClosureReason(Entity closedBy, out string reason)
        {
            reason = null;
            return closedBy != Entity.Null && m_ClosureReasons.TryGetValue(closedBy, out reason);
        }

        /// <summary>What closed the airport, for people: an add-on's reason, the disaster's name, or empty.</summary>
        internal string DescribeClosure(Entity closedBy)
        {
            if (TryGetClosureReason(closedBy, out string reason))
            {
                return reason;
            }
            return AtcRadio.GetEventName(EntityManager, m_PrefabSystem, closedBy);
        }

        internal bool IsRunwayClosedByAddOn(Entity runway)
        {
            return m_ClosureReasons.ContainsKey(runway);
        }

        // ---- Operating values

        internal void SetOverrides(Entity airport, in OperatingOverrides overrides)
        {
            m_Overrides[airport] = overrides;
            m_ApiChanged = true;
        }

        internal void ClearOverrides(Entity airport)
        {
            m_ApiChanged |= m_Overrides.Remove(airport);
        }

        internal bool TryGetOverrides(Entity airport, out OperatingOverrides overrides)
        {
            return m_Overrides.TryGetValue(airport, out overrides);
        }

        /// <summary>The values the tower uses at an airport now: the options, the climate's go-around chance, an add-on's overrides.</summary>
        internal OperatingValues GetOperatingValues(Entity airport)
        {
            AtcSettings settings = m_Overrides.TryGetValue(airport, out OperatingOverrides overrides) ? ToJob(overrides).Apply(m_LastSettings) : m_LastSettings;
            return new OperatingValues
            {
                ArrivalSpacingSeconds = settings.m_ArrivalSpacingSeconds,
                DepartureGapSeconds = settings.m_DepartureGapSeconds,
                FinalLengthMeters = settings.m_FinalLength,
                PatternWidthMeters = settings.m_PatternWidth,
                WeatherGoAroundChance = settings.m_WeatherGoAroundChance,
                GlidePath = settings.m_GlidePath,
                GlidePathDegrees = math.degrees(math.atan(settings.m_GlideTan)),
                InstrumentApproach = settings.m_GlidePath,
                StandardArrival = settings.m_StandardArrival && settings.m_GlidePath,
                StandardDeparture = settings.m_SmoothDepartureTurns,
                ApproachHeightLimits = settings.m_ApproachHeightLimits && settings.m_GlidePath,
                WeatherGoArounds = settings.m_WeatherGoArounds,
                HoldingLegLengthMeters = settings.m_HoldingLegLength,
                HoldShortSetbackMeters = settings.m_HoldShortSetback,
                TaxiSpacingMeters = settings.m_TaxiSpacing,
                Pushback = settings.m_Pushback,
                PushbackDistanceMeters = settings.m_PushbackDistance,
                CloseForTornado = (settings.m_ClosureHazards & HazardKinds.Tornado) != 0,
                CloseForHail = (settings.m_ClosureHazards & HazardKinds.Hail) != 0,
                CloseForOtherDisasters = (settings.m_ClosureHazards & HazardKinds.Other) != 0,
                MaxClosureMinutes = settings.m_MaxClosureFrames / Setting.kFramesPerGameMinute,
            };
        }

        // ---- Flights

        internal void SetHeld(Entity airplane, bool held)
        {
            m_ApiChanged |= held ? m_Held.Add(airplane) : m_Held.Remove(airplane);
        }

        internal void SetPriority(Entity airplane, bool priority)
        {
            m_ApiChanged |= priority ? m_Priority.Add(airplane) : m_Priority.Remove(airplane);
        }

        internal void QueueGoAround(Entity airplane, string reason)
        {
            FixedString128Bytes text = default;
            text.CopyFromTruncated(string.IsNullOrWhiteSpace(reason) ? "requested" : reason.Trim());
            m_PendingGoArounds.Add(new ApiGoAround { m_Airplane = airplane, m_Reason = text });
        }

        internal bool TryGetFlightInfo(Entity airplane, out FlightInfo info)
        {
            m_JobHandle.Complete();
            if (!m_Flights.TryGetValue(airplane, out Flight flight))
            {
                info = default;
                return false;
            }
            info = ToInfo(airplane, flight);
            return true;
        }

        /// <summary>The flights the tower handles at an airport (every airport for Entity.Null), added to <paramref name="flights"/>.</summary>
        internal void GetFlights(Entity airport, List<FlightInfo> flights)
        {
            m_JobHandle.Complete();
            foreach (var pair in m_Flights)
            {
                FlightInfo info = ToInfo(pair.Key, pair.Value);
                if (airport == Entity.Null || info.Airport == airport)
                {
                    flights.Add(info);
                }
            }
        }

        private FlightInfo ToInfo(Entity airplane, in Flight flight)
        {
            Entity airport = m_Runways.TryGetValue(flight.m_Runway, out RunwayState state) ? state.m_Airport : Entity.Null;
            // Cleared before holding: a cleared arrival still flies its hold for a moment before it turns back to its path,
            // and an add-on must not take it for one still waiting.
            FlightState flightState = flight.m_InCircuit ? FlightState.InPattern
                : flight.m_Clearance == Clearance.Cleared ? FlightState.Cleared
                : flight.m_Clearance == Clearance.LineUp ? FlightState.LineUp
                : flight.m_Holding ? FlightState.Holding
                : FlightState.Waiting;
            return new FlightInfo
            {
                Airplane = airplane,
                Airport = airport,
                Runway = flight.m_Runway,
                IsArrival = flight.m_Arrival,
                IsCrossing = flight.m_Crossing,
                State = flightState,
                WaitedSeconds = (m_SimulationSystem.frameIndex - flight.m_RequestFrame) / 60f,
                EtaSeconds = flight.m_LastEta,
                HoldAltitudeMeters = flight.m_Holding ? flight.m_Pattern.m_Fix.y - flight.m_EntryPosition.y : 0f,
                IsHeld = m_Held.Contains(airplane),
                HasPriority = m_Priority.Contains(airplane),
            };
        }

        // ---- Radio

        /// <summary>Adds an add-on's transmission to the radio log, as if the tower had said it this frame.</summary>
        internal void PostRadio(Entity airport, AtcFrequency frequency, Entity airplane, string callsign, string controller, string pilot, bool pilotFirst)
        {
            m_JobHandle.Complete();
            int id = m_Radio.AddCustomText(callsign ?? string.Empty, controller ?? string.Empty, pilot ?? string.Empty, pilotFirst, kRadioLogCapacity * 2);
            AtcMessage message = new AtcMessage
            {
                m_Airplane = airplane,
                m_Airport = airport,
                m_Frame = m_SimulationSystem.frameIndex,
                m_Kind = AtcMessageKind.Custom,
                m_Frequency = frequency,
                m_Value = id,
            };
            m_RadioVersion.Value++;
            if (m_RadioLog.Length >= kRadioLogCapacity)
            {
                m_RadioLog.RemoveAt(0);
            }
            m_RadioLog.Add(message);
            if (airplane != Entity.Null)
            {
                m_LastMessages[airplane] = message;
            }
        }

        // ---- Airports and runways

        internal bool TryGetAirportInfo(Entity airport, out AirportInfo info)
        {
            info = default;
            if (!m_Airspaces.TryGetValue(airport, out AirportSpace space))
            {
                return false;
            }
            Entity closedBy = GetClosure(airport);
            bool groundDelay = GetGroundDelay(airport, out int groundTraffic);
            info = new AirportInfo
            {
                Airport = airport,
                Code = AirportCodes.Get(EntityManager, NameSystem, airport),
                Class = space.m_Class,
                ClassChosen = space.m_Chosen,
                Center = space.m_Center,
                Elevation = space.m_Height,
                RadiusMeters = space.m_Radius,
                HoldBaseMeters = space.HoldBase,
                Runways = space.m_Runways,
                Gates = space.m_Gates,
                IsClosed = closedBy != Entity.Null,
                ClosedBy = closedBy,
                ClosureReason = closedBy != Entity.Null ? DescribeClosure(closedBy) : string.Empty,
                IsClosedByAddOn = closedBy != Entity.Null && m_ClosureReasons.ContainsKey(closedBy),
                GroundDelay = groundDelay,
                GroundTraffic = groundTraffic,
                LineUpAndWait = AirportSettings.IsLineUpEnabled(EntityManager, this, airport),
            };
            return true;
        }

        /// <summary>The runway ends in use at an airport (every airport for Entity.Null), added to <paramref name="ends"/>.</summary>
        internal void GetRunwayEnds(Entity airport, List<RunwayEndInfo> ends)
        {
            for (int i = 0; i < m_RunwayList.Count; i++)
            {
                RunwayInfo runway = m_RunwayList[i];
                if (airport != Entity.Null && runway.Airport != airport)
                {
                    continue;
                }
                RunwayGeometry geometry = runway.Geometry;
                byte used = RunwayNames.UsedEnds(geometry);
                if ((used & RunwayGeometry.kAlong) != 0)
                {
                    ends.Add(ToEndInfo(runway, geometry.m_Direction));
                }
                if ((used & RunwayGeometry.kAgainst) != 0)
                {
                    ends.Add(ToEndInfo(runway, -geometry.m_Direction));
                }
            }
        }

        /// <summary>Height of the final approach fix above the runway (AtcJob.kFinalFixHeight).</summary>
        private const float kApproachFixHeight = 1500f * 0.3048f;

        /// <summary>The approach of every runway end in use at an airport flying the glide path (see <see cref="ApproachZoneSystem"/>).</summary>
        internal void GetApproachCorridors(List<ApproachCorridor> corridors)
        {
            // Where the "Approach height limits" option (or an add-on's override) is off: no surfaces, and those applied are
            // given back to the game.
            if (Mod.Setting == null)
            {
                return;
            }
            List<RunwayEndInfo> ends = new List<RunwayEndInfo>();
            GetRunwayEnds(Entity.Null, ends);
            for (int i = 0; i < ends.Count; i++)
            {
                OperatingValues values = GetOperatingValues(ends[i].Airport);
                if (!values.ApproachHeightLimits)
                {
                    continue;
                }
                float glideTan = math.tan(math.radians(values.GlidePathDegrees));
                corridors.Add(new ApproachCorridor
                {
                    m_Threshold = ends[i].Start.xz,
                    m_Outward = -ends[i].Direction,
                    m_Height = ends[i].Start.y,
                    m_GlideTan = glideTan,
                    // Out to the final approach fix; beyond it arrivals stay at its height.
                    m_Length = kApproachFixHeight / math.max(glideTan, 0.01f),
                });
            }
        }

        private RunwayEndInfo ToEndInfo(RunwayInfo runway, float2 direction)
        {
            RunwayGeometry geometry = runway.Geometry;
            // The end an airplane starts from in this direction: the low end of the axis going along it, else the high end.
            float along = math.dot(direction, geometry.m_Direction) > 0f ? geometry.m_Min : geometry.m_Max;
            float2 start = geometry.m_Origin + geometry.m_Direction * along;
            float height = m_Airspaces.TryGetValue(runway.Airport, out AirportSpace space) ? space.m_Height : 0f;
            bool closed = m_Runways.TryGetValue(runway.Key, out RunwayState state) && state.m_ClosedBy != Entity.Null;
            return new RunwayEndInfo
            {
                Runway = runway.Key,
                Airport = runway.Airport,
                Name = RunwayNames.Name(m_RunwayList, runway.Key, direction),
                Start = new float3(start.x, height, start.y),
                Direction = direction,
                LengthMeters = geometry.m_Max - geometry.m_Min,
                RightHandPattern = AirportSettings.IsRightHand(EntityManager, this, runway, direction),
                IsClosed = closed,
            };
        }

        internal RunwayInfo FindRunway(Entity runway)
        {
            for (int i = 0; i < m_RunwayList.Count; i++)
            {
                if (m_RunwayList[i].Key == runway)
                {
                    return m_RunwayList[i];
                }
            }
            return null;
        }

        internal AtcSettings LastSettings => m_LastSettings;
    }
}
