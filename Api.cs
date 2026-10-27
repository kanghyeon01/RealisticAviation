using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>One radio transmission (a tower instruction and its readback, a broadcast to all aircraft, or an add-on's post).</summary>
    public readonly struct RadioMessage
    {
        internal readonly AtcMessage m_Message;

        internal RadioMessage(in AtcMessage message)
        {
            m_Message = message;
        }

        public AtcMessageKind Kind => m_Message.m_Kind;

        /// <summary>The frequency it was said on. A broadcast is heard on every frequency of its airport.</summary>
        public AtcFrequency Frequency => m_Message.m_Frequency;

        /// <summary>The airplane spoken to, or Entity.Null for a broadcast to all aircraft.</summary>
        public Entity Airplane => m_Message.m_Airplane;

        public Entity Airport => m_Message.m_Airport;

        /// <summary>The runway it is about, or Entity.Null.</summary>
        public Entity Runway => m_Message.m_Runway;

        /// <summary>What closed the airport for <see cref="AtcMessageKind.AirportClosed"/>; the next airport for a handoff; else usually Entity.Null.</summary>
        public Entity Event => m_Message.m_Event;

        /// <summary>The simulation frame it was said in.</summary>
        public uint Frame => m_Message.m_Frame;

        public bool IsBroadcast => m_Message.m_Airplane == Entity.Null;

        /// <summary>A takeoff clearance with an arrival close behind: "cleared for takeoff, no delay".</summary>
        public bool NoDelay => m_Message.m_Kind == AtcMessageKind.ClearedForTakeoff && m_Message.m_Value == 1;
    }

    /// <summary>
    /// Why a pilot goes around on its own, said with "going around" (the tower's go-around gives its own reason). Which one
    /// is picked by weight for the weather (Api.Radio.SetGoAroundReasonWeight). API version 4; new reasons come at the end.
    /// </summary>
    public enum GoAroundReason : byte
    {
        /// <summary>"unstable approach": too high or too fast on final.</summary>
        UnstableApproach,

        /// <summary>"runway not in sight" at minimums.</summary>
        RunwayNotInSight,

        /// <summary>"windshear".</summary>
        Windshear,

        /// <summary>"gusty winds".</summary>
        GustyWinds,

        /// <summary>"wake turbulence" from the airplane ahead.</summary>
        WakeTurbulence,

        /// <summary>"birds on the runway".</summary>
        BirdsOnRunway,
    }

    /// <summary>Where a flight stands with the tower.</summary>
    public enum FlightState : byte
    {
        /// <summary>Asked for its runway and waits: short of it on the ground, or in the air until it is put in a hold.</summary>
        Waiting,
        /// <summary>Flying a holding pattern, waiting for its clearance.</summary>
        Holding,
        /// <summary>Flying the traffic pattern after a go-around.</summary>
        InPattern,
        /// <summary>Cleared to line up and wait behind traffic.</summary>
        LineUp,
        /// <summary>Cleared to land, take off or cross: the runway is reserved for it (also while it leaves a hold).</summary>
        Cleared,
    }

    /// <summary>A flight the tower handles: one airplane using (or about to use) one runway.</summary>
    public struct FlightInfo
    {
        public Entity Airplane;
        public Entity Airport;
        public Entity Runway;
        public bool IsArrival;

        /// <summary>A ground airplane that only crosses the runway.</summary>
        public bool IsCrossing;
        public FlightState State;

        /// <summary>Game seconds since it asked for the runway.</summary>
        public float WaitedSeconds;

        /// <summary>For an arrival: estimated game seconds to the runway, as of the last tower update.</summary>
        public float EtaSeconds;

        /// <summary>Height of its hold above the runway, in metres, while holding; else 0.</summary>
        public float HoldAltitudeMeters;

        /// <summary>An add-on holds its clearance back (Api.Flights.HoldClearance).</summary>
        public bool IsHeld;

        /// <summary>An add-on gave it priority (Api.Flights.SetPriority).</summary>
        public bool HasPriority;
    }

    /// <summary>An airport as the tower sees it.</summary>
    public struct AirportInfo
    {
        public Entity Airport;

        /// <summary>The ICAO code: entered by the player (or an add-on), or made from the name.</summary>
        public string Code;
        public AirspaceClass Class;

        /// <summary>The class was chosen (in the panel or by an add-on) rather than set from the airport's size.</summary>
        public bool ClassChosen;

        /// <summary>Centre of the airspace on the map (x, z), and the runways' ground height.</summary>
        public float2 Center;
        public float Elevation;
        public float RadiusMeters;

        /// <summary>Height of the lowest hold above the runway, in metres.</summary>
        public float HoldBaseMeters;
        public int Runways;
        public int Gates;
        public bool IsClosed;

        /// <summary>What closed it: a disaster event, the airport or runway an add-on closed, or the airport itself.</summary>
        public Entity ClosedBy;

        /// <summary>The add-on's reason, or the disaster's name, as shown in the panel and said on the radio.</summary>
        public string ClosureReason;
        /// <summary>Closed by an add-on (the airport, or one of its runways), with <see cref="ClosureReason"/> its reason.</summary>
        public bool IsClosedByAddOn;

        /// <summary>A ground delay is in effect (no new arrivals until the ground traffic thins out).</summary>
        public bool GroundDelay;

        /// <summary>Airplanes moving on its ground (gates not counted), as of the last tower update.</summary>
        public int GroundTraffic;
        public bool LineUpAndWait;

        /// <summary>The airport has a VOR on the IFR charts (API 5).</summary>
        public bool Vor;

        /// <summary>Long pushback is on at the airport (API 5).</summary>
        public bool LongPushback;
    }

    /// <summary>One end of a runway in use: where airplanes start their landing or takeoff roll, and which way they go.</summary>
    public struct RunwayEndInfo
    {
        /// <summary>The runway (its key edge): the same entity for both of its ends.</summary>
        public Entity Runway;
        public Entity Airport;

        /// <summary>As on the radio: "27", "09L".</summary>
        public string Name;
        public float3 Start;

        /// <summary>Unit direction of travel on the map (x, z).</summary>
        public float2 Direction;
        public float LengthMeters;
        public bool RightHandPattern;
        public bool IsClosed;
    }

    /// <summary>
    /// An airport's operating values set by an add-on, over the options. A value left null uses the option. Not saved with
    /// the city: set them again after a city is loaded.
    /// </summary>
    public struct OperatingOverrides
    {
        /// <summary>Minimum time between two arrivals cleared to the same runway.</summary>
        public float? ArrivalSpacingSeconds;

        /// <summary>A ground airplane may go ahead of an arrival at least this far out.</summary>
        public float? DepartureGapSeconds;

        /// <summary>Straight-in finals from the final approach fix at <see cref="GlidePathDegrees"/>.</summary>
        public bool? GlidePath;
        public float? GlidePathDegrees;

        /// <summary>Final length after a go-around when the glide path is off.</summary>
        public float? FinalLengthMeters;
        public float? PatternWidthMeters;

        /// <summary>Chance (0–1) that an arrival goes around at the decision height on its own; replaces the weather's.</summary>
        public float? WeatherGoAroundChance;

        // Added in API version 2 (1.2.2): every option that can be set per airport. Null keeps the option.

        /// <summary>The instrument approach procedure (the same as <see cref="GlidePath"/>, which counts when this is null).</summary>
        public bool? InstrumentApproach;

        /// <summary>Held arrivals hold at their IAF (only with the instrument approach).</summary>
        public bool? StandardArrival;

        /// <summary>Departures fly the SID (standard rate turn, direct to a fix on their airway).</summary>
        public bool? StandardDeparture;

        /// <summary>Building height limits under the approaches of this airport's runways (with the instrument approach).</summary>
        public bool? ApproachHeightLimits;

        /// <summary>Weather go-arounds: off makes the chance 0, on uses the climate's; <see cref="WeatherGoAroundChance"/> replaces either.</summary>
        public bool? WeatherGoArounds;

        /// <summary>Straight-leg length of holding patterns.</summary>
        public float? HoldingLegLengthMeters;

        /// <summary>Gap between a waiting airplane's nose and the runway.</summary>
        public float? HoldShortSetbackMeters;

        /// <summary>Gap a taxiing airplane keeps behind the airplane ahead; 0 turns the taxi spacing off.</summary>
        public float? TaxiSpacingMeters;

        /// <summary>Pushback out of nose-in gates.</summary>
        public bool? Pushback;

        /// <summary>How far an airplane is pushed back (by default half its length, 15–40 m). Clamped to 5–100 m.</summary>
        public float? PushbackDistanceMeters;

        /// <summary>Which disasters close the airport.</summary>
        public bool? CloseForTornado;
        public bool? CloseForHail;
        public bool? CloseForOtherDisasters;

        /// <summary>A closure for one disaster ends after <see cref="MaxClosureMinutes"/> game minutes.</summary>
        public bool? LimitClosureTime;
        public float? MaxClosureMinutes;

        // API version 4 (1.2.4).

        /// <summary>
        /// An arrival crosses the threshold behind a departure that has lifted off only once it is this far past the threshold
        /// (default 1,829 m, FAA 6,000 ft), or <see cref="DepartureAheadShare"/> of a shorter runway, or past the runway end.
        /// </summary>
        public float? DepartureAheadMeters;

        /// <summary>That distance as a share of the runway length, used when shorter (default 0.6). Clamped to 0–1.</summary>
        public float? DepartureAheadShare;

        // API version 5 (IFR chart update).

        /// <summary>
        /// Minimum obstacle clearance of the initial segments and the STAR holds (default 1,000 ft, PANS-OPS) and of the
        /// intermediate segment (default 500 ft): the tower raises the IF, the IAFs and the hold bases until obstacles under
        /// them have this much. Not below 0.
        /// </summary>
        public float? InitialMocFeet;
        public float? IntermediateMocFeet;

        /// <summary>
        /// Width of the obstacle areas each side of a segment: the primary area (full MOC, default 1,000 m) and the secondary
        /// area outside it (MOC tapering to 0 at its edge, default 1,000 m). Not below 0.
        /// </summary>
        public float? ObstacleAreaPrimaryMeters;
        public float? ObstacleAreaSecondaryMeters;

        /// <summary>
        /// Height above the runway where an arrival decides to land or go around (default 200 ft, CAT I). Clamped to 50–1,000 ft.
        /// </summary>
        public float? DecisionHeightFeet;
    }

    /// <summary>The operating values in use at an airport: the options, the weather and any add-on's overrides.</summary>
    public struct OperatingValues
    {
        public float ArrivalSpacingSeconds;
        public float DepartureGapSeconds;
        public bool GlidePath;
        public float GlidePathDegrees;
        public float FinalLengthMeters;
        public float PatternWidthMeters;
        public float WeatherGoAroundChance;

        // API version 2 (1.2.2).
        public bool InstrumentApproach;
        public bool StandardArrival;
        public bool StandardDeparture;
        public bool ApproachHeightLimits;
        public bool WeatherGoArounds;
        public float HoldingLegLengthMeters;
        public float HoldShortSetbackMeters;
        public float TaxiSpacingMeters;
        public bool Pushback;

        /// <summary>The fixed pushback distance, or 0 for the default (half the airplane's length, 15–40 m).</summary>
        public float PushbackDistanceMeters;
        public bool CloseForTornado;
        public bool CloseForHail;
        public bool CloseForOtherDisasters;

        /// <summary>Longest closure for one disaster in game minutes; 0 = as long as the disaster lasts.</summary>
        public float MaxClosureMinutes;

        // API version 4 (1.2.4).
        public float DepartureAheadMeters;
        public float DepartureAheadShare;

        // API version 5.
        public float InitialMocFeet;
        public float IntermediateMocFeet;
        public float ObstacleAreaPrimaryMeters;
        public float ObstacleAreaSecondaryMeters;
        public float DecisionHeightFeet;
    }

    /// <summary>
    /// The public interface of Realistic Aviation for add-ons, by area: <see cref="Airports"/>, <see cref="Runways"/>,
    /// <see cref="Flights"/>, <see cref="Radio"/>, <see cref="Events"/>, <see cref="Atis"/> and <see cref="Options"/>.
    /// Everything here is for the main thread only (a system's OnUpdate, a UI callback). Orders and overrides reach the tower
    /// at its next update (at most 16 simulation frames later). Other public types of this assembly are not part of it and may
    /// change in any version; this interface only grows (see docs/AddonGuide.md).
    /// </summary>
    public static class Api
    {
        /// <summary>
        /// The version of this interface: raised when something is added, so an add-on can check for what it needs. A property,
        /// not a constant, so an add-on reads the version of the Realistic Aviation that is running, not the one it was built with.
        /// </summary>
        public static int Version => 5;

        /// <summary>The tower is running (a city is loaded or loading). Until then the members return nothing.</summary>
        public static bool IsReady => Tower != null;

        /// <summary>Gives an airport's ATIS to the tower, for "with information X".</summary>
        public delegate bool AtisProvider(Entity airport, out AtisState state);

        /// <summary>Returns the callsign to use for an airplane, or null to keep Realistic Aviation's.</summary>
        public delegate string CallsignProvider(Entity airplane, string callsign);

        /// <summary>Changes the words of a transmission (without the "ATC:" or callsign speaker labels); the pilot's is empty for a broadcast.</summary>
        public delegate void PhraseProvider(RadioMessage message, ref string controller, ref string pilot);

        internal static AtcSystem Tower { get; set; }

        private static void LogHandlerError(Delegate handler, Exception e)
        {
            Mod.Log.Error(e, $"An add-on's handler failed: {handler.Method.DeclaringType?.FullName}.{handler.Method.Name}");
        }

        private static void Register<T>(List<T> list, T item) where T : class
        {
            if (item != null)
            {
                list.Remove(item);
                list.Add(item);
            }
        }

        /// <summary>Airports: information, saved settings, closures and operating values.</summary>
        public static class Airports
        {
            /// <summary>Every airport in the city, once each: top-level buildings with airplane stops, outside connections excluded.</summary>
            public static void GetAll(List<Entity> airports)
            {
                airports.Clear();
                if (Tower == null)
                {
                    return;
                }
                NativeList<Entity> found = new NativeList<Entity>(4, Allocator.Temp);
                try
                {
                    RealisticAviation.Airports.Collect(Tower.EntityManager, Tower.AirplaneStopQuery, found);
                    for (int i = 0; i < found.Length; i++)
                    {
                        airports.Add(found[i]);
                    }
                }
                finally
                {
                    found.Dispose();
                }
            }

            /// <summary>
            /// The airport a structure belongs to (the airport itself, its roads, runways and stops; not its upgrades), or Entity.Null.
            /// </summary>
            public static Entity Find(Entity entity)
            {
                return Tower == null ? Entity.Null : RealisticAviation.Airports.GetAirport(Tower.EntityManager, Tower.AirplaneStopQuery, entity);
            }

            /// <summary>The airport as the tower sees it, or false if it has no airspace yet (not an airport, or its runways not found yet).</summary>
            public static bool TryGetInfo(Entity airport, out AirportInfo info)
            {
                info = default;
                return Tower != null && Tower.TryGetAirportInfo(airport, out info);
            }

            public static bool TryGetAirspaceClass(Entity airport, out AirspaceClass airspaceClass)
            {
                airspaceClass = default;
                return Tower != null && Tower.TryGetAirspaceClass(airport, out airspaceClass);
            }

            /// <summary>Sets the airport's airspace class (saved). Applied on the next frame; <see cref="Events.AirspaceClassChanged"/> follows.</summary>
            public static void SetAirspaceClass(Entity airport, AirspaceClass airspaceClass)
            {
                Tower?.RequestAirspaceClass(airport, (int)airspaceClass);
            }

            /// <summary>Lets the airport's class follow its size again (saved).</summary>
            public static void SetAutomaticAirspaceClass(Entity airport)
            {
                Tower?.RequestAirspaceClass(airport, -1);
            }

            /// <summary>The ICAO code: entered, or made from the name.</summary>
            public static string GetCode(Entity airport)
            {
                return Tower == null ? null : AirportCodes.Get(Tower.EntityManager, Tower.NameSystem, airport);
            }

            /// <summary>Sets the code (saved): 3–4 letters or digits; null or empty brings back the automatic one. False if not valid.</summary>
            public static bool SetCode(Entity airport, string code)
            {
                return Tower != null && Tower.EntityManager.Exists(airport) && AirportSettings.SetAirportCode(Tower.EntityManager, airport, code);
            }

            /// <summary>Line up and wait at the airport (saved, as a choice for its present class).</summary>
            public static void SetLineUpAndWait(Entity airport, bool enabled)
            {
                if (Tower != null && Tower.EntityManager.Exists(airport))
                {
                    AirportSettings.SetLineUp(Tower.EntityManager, Tower, airport, enabled);
                }
            }

            /// <summary>A VOR at the airport on the IFR charts (saved, as a choice for its present class). API 5.</summary>
            public static void SetVor(Entity airport, bool enabled)
            {
                if (Tower != null && Tower.EntityManager.Exists(airport))
                {
                    AirportSettings.SetVor(Tower.EntityManager, Tower, airport, enabled);
                }
            }

            /// <summary>Long pushback at the airport (saved): airplanes push back about their own length. API 5.</summary>
            public static void SetLongPushback(Entity airport, bool enabled)
            {
                if (Tower != null && Tower.EntityManager.Exists(airport))
                {
                    AirportSettings.SetLongPushback(Tower.EntityManager, airport, enabled);
                }
            }

            /// <summary>The ground delay setting: whether it is on, and how many airplanes on the ground start it.</summary>
            public static bool GetGroundDelay(Entity airport, out int limit)
            {
                limit = 0;
                if (Tower == null || !Tower.EntityManager.Exists(airport))
                {
                    return false;
                }
                GroundDelaySetting setting = AirportSettings.GetGroundDelay(Tower.EntityManager, Tower, airport);
                limit = setting.m_Limit;
                return setting.m_Enabled;
            }

            /// <summary>Sets the ground delay (saved); the limit is kept within the panel's range.</summary>
            public static void SetGroundDelay(Entity airport, bool enabled, int limit)
            {
                if (Tower != null && Tower.EntityManager.Exists(airport))
                {
                    AirportSettings.SetGroundDelay(Tower.EntityManager, airport, new GroundDelaySetting
                    {
                        m_Enabled = enabled,
                        m_Limit = (byte)math.clamp(limit, GroundDelaySetting.kMinLimit, GroundDelaySetting.kMaxLimit),
                    });
                }
            }

            /// <summary>
            /// Closes the airport until <see cref="Open"/>: arrivals hold, departures hold short, as for a disaster. The reason
            /// shows in the panel and on the radio ("airport closed due to ..."). Not saved.
            /// </summary>
            public static void Close(Entity airport, string reason)
            {
                Tower?.SetClosure(airport, reason);
            }

            /// <summary>Lifts this add-on closure (a disaster may still keep it closed). False if it wasn't closed by an add-on.</summary>
            public static bool Open(Entity airport)
            {
                return Tower != null && Tower.ClearClosure(airport);
            }

            /// <summary>Whether the airport is closed, and what closed it (a disaster event, or the airport or runway an add-on closed).</summary>
            public static bool IsClosed(Entity airport, out Entity closedBy)
            {
                closedBy = Entity.Null;
                return Tower != null && Tower.IsClosed(airport, out closedBy);
            }

            /// <summary>The runway ends in use for landing, named as on the radio ("27", "09L"), sorted, added to <paramref name="names"/>.</summary>
            public static void GetLandingRunways(Entity airport, List<string> names)
            {
                Tower?.GetLandingRunways(airport, names);
            }

            /// <summary>Overrides the airport's operating values (not saved); null fields keep the options.</summary>
            public static void SetOverrides(Entity airport, OperatingOverrides overrides)
            {
                Tower?.SetOverrides(airport, overrides);
            }

            public static void ClearOverrides(Entity airport)
            {
                Tower?.ClearOverrides(airport);
            }

            public static bool TryGetOverrides(Entity airport, out OperatingOverrides overrides)
            {
                overrides = default;
                return Tower != null && Tower.TryGetOverrides(airport, out overrides);
            }

            /// <summary>The values the tower uses at the airport now (options, weather, overrides).</summary>
            public static OperatingValues GetOperatingValues(Entity airport)
            {
                return Tower == null ? default : Tower.GetOperatingValues(airport);
            }
        }

        /// <summary>Runways: their ends, closures and pattern sides.</summary>
        public static class Runways
        {
            /// <summary>The runway ends in use at an airport (every airport for Entity.Null), added to <paramref name="ends"/>.</summary>
            public static void GetEnds(Entity airport, List<RunwayEndInfo> ends)
            {
                Tower?.GetRunwayEnds(airport, ends);
            }

            /// <summary>Closes one runway (<see cref="RunwayEndInfo.Runway"/>) until <see cref="Open"/>, with a reason. Not saved.</summary>
            public static void Close(Entity runway, string reason)
            {
                Tower?.SetClosure(runway, reason);
            }

            public static bool Open(Entity runway)
            {
                return Tower != null && Tower.ClearClosure(runway);
            }

            /// <summary>Sets the traffic pattern side for the runway end landing in <paramref name="direction"/> (saved). False if no such runway.</summary>
            public static bool SetRightHandPattern(Entity runway, float2 direction, bool rightHand)
            {
                RunwayInfo info = Tower?.FindRunway(runway);
                if (info == null)
                {
                    return false;
                }
                AirportSettings.SetRightHand(Tower.EntityManager, info, math.normalizesafe(direction), rightHand);
                return true;
            }
        }

        /// <summary>Flights: what the tower is doing with each airplane, and orders for it.</summary>
        public static class Flights
        {
            /// <summary>The flights at an airport (every airport for Entity.Null), added to <paramref name="flights"/>.</summary>
            public static void GetAll(Entity airport, List<FlightInfo> flights)
            {
                Tower?.GetFlights(airport, flights);
            }

            public static bool TryGet(Entity airplane, out FlightInfo info)
            {
                info = default;
                return Tower != null && Tower.TryGetFlightInfo(airplane, out info);
            }

            /// <summary>Sends an arrival on final around (with the reason in the log). Ignored for any other airplane.</summary>
            public static void GoAround(Entity airplane, string reason)
            {
                Tower?.QueueGoAround(airplane, reason);
            }

            /// <summary>
            /// Holds back (or no longer) the airplane's next clearance: an arrival keeps holding, a departure keeps holding
            /// short. A clearance it already has is kept. Not saved.
            /// </summary>
            public static void HoldClearance(Entity airplane, bool hold)
            {
                Tower?.SetHeld(airplane, hold);
            }

            /// <summary>Puts the airplane ahead of every other one waiting for its runway (or no longer). Not saved.</summary>
            public static void SetPriority(Entity airplane, bool priority)
            {
                Tower?.SetPriority(airplane, priority);
            }
        }

        /// <summary>Radio: the log, the words, posting, and callsign and phrase providers.</summary>
        public static class Radio
        {
            private static readonly List<CallsignProvider> s_CallsignProviders = new List<CallsignProvider>();
            private static readonly List<PhraseProvider> s_PhraseProviders = new List<PhraseProvider>();
            private static readonly List<AtcMessage> s_Buffer = new List<AtcMessage>();

            /// <summary>The latest transmissions on one of the airport's frequencies (broadcasts on all), newest first, at most <paramref name="max"/>.</summary>
            public static void GetLog(Entity airport, AtcFrequency frequency, List<RadioMessage> messages, int max)
            {
                messages.Clear();
                if (Tower == null)
                {
                    return;
                }
                Tower.GetRadioLog(airport, frequency, s_Buffer, max);
                for (int i = 0; i < s_Buffer.Count; i++)
                {
                    messages.Add(new RadioMessage(s_Buffer[i]));
                }
            }

            /// <summary>The airplane's latest transmission, if it heard one recently.</summary>
            public static bool TryGetLastMessage(Entity airplane, out RadioMessage message)
            {
                if (Tower != null && Tower.TryGetLastMessage(airplane, out AtcMessage last))
                {
                    message = new RadioMessage(last);
                    return true;
                }
                message = default;
                return false;
            }

            /// <summary>
            /// The lines of a transmission as the info panels show them ("ATC: ..." and "callsign: ..."), the first one said first;
            /// <paramref name="pilotFirst"/> when the pilot called first. The second line is empty for a broadcast.
            /// </summary>
            public static void GetText(in RadioMessage message, out string first, out string second, out bool pilotFirst)
            {
                if (Tower == null)
                {
                    first = string.Empty;
                    second = string.Empty;
                    pilotFirst = false;
                    return;
                }
                Tower.Radio.Describe(Tower.EntityManager, Tower.NameSystem, Tower.PrefabSystem, Tower.RunwayList, message.m_Message,
                    message.Frequency, out first, out second, out pilotFirst);
            }

            /// <summary>
            /// Adds a transmission of your own to the airport's radio log, shown in the panels like the tower's: the controller's and
            /// the pilot's words (either may be empty) and who spoke first. With an airplane (any entity, a helicopter too) it is
            /// heard on <paramref name="frequency"/> only; without one, on every frequency, as a broadcast.
            /// </summary>
            public static void Post(Entity airport, AtcFrequency frequency, Entity airplane, string callsign, string controller, string pilot, bool pilotFirst)
            {
                Tower?.PostRadio(airport, frequency, airplane, callsign, controller, pilot, pilotFirst);
            }

            /// <summary>
            /// How often a pilot's own go-around gives <paramref name="reason"/> in <paramref name="weather"/>, relative to the
            /// other reasons in that weather (city-wide). 0 never gives it; with every weight 0 the pilot just says "going around".
            /// Not saved: back to the defaults when a city is loaded. Version 4.
            /// </summary>
            public static void SetGoAroundReasonWeight(GoAroundReason reason, WeatherKind weather, float weight)
            {
                GoAroundReasons.Set(reason, weather, weight);
            }

            /// <summary>The weight in use (see <see cref="SetGoAroundReasonWeight"/>). Version 4.</summary>
            public static float GetGoAroundReasonWeight(GoAroundReason reason, WeatherKind weather)
            {
                return GoAroundReasons.Get(reason, weather);
            }

            /// <summary>
            /// The city's telephone area code in the tower's phone number (pilot deviation calls), 3 digits: NXX, N 2–9, X 0–8,
            /// not N11. The number is always &lt;area code&gt;-555-01XX, a range set aside for fiction. Reads the one in use;
            /// set null, or a code that isn't valid, for the one made up from the city's name. Not saved. Version 3.
            /// </summary>
            public static string AreaCode
            {
                get => AtcRadio.AreaCode();
                set
                {
                    string code = value?.Trim();
                    AtcRadio.s_AreaCode = AtcRadio.IsAreaCode(code) ? code : null;
                }
            }

            /// <summary>Supplies the callsigns; the latest registered is asked.</summary>
            public static void RegisterCallsignProvider(CallsignProvider provider)
            {
                Register(s_CallsignProviders, provider);
            }

            public static void UnregisterCallsignProvider(CallsignProvider provider)
            {
                s_CallsignProviders.Remove(provider);
            }

            /// <summary>Changes the words of every transmission; all registered ones are asked, in the order they registered.</summary>
            public static void RegisterPhraseProvider(PhraseProvider provider)
            {
                Register(s_PhraseProviders, provider);
            }

            public static void UnregisterPhraseProvider(PhraseProvider provider)
            {
                s_PhraseProviders.Remove(provider);
            }

            internal static CallsignProvider CurrentCallsignProvider => s_CallsignProviders.Count == 0 ? null : s_CallsignProviders[s_CallsignProviders.Count - 1];

            internal static IReadOnlyList<PhraseProvider> PhraseProviders => s_PhraseProviders;
        }

        /// <summary>
        /// Events, raised on the main thread once per tower update (every 16 simulation frames), in the order things were said.
        /// Each handler runs on its own: one that throws is logged and skipped.
        /// </summary>
        public static class Events
        {
            /// <summary>Every transmission, an add-on's posts included.</summary>
            public static event Action<RadioMessage> MessageSent;

            /// <summary>An arrival is cleared for the approach.</summary>
            public static event Action<RadioMessage> ApproachCleared;

            /// <summary>An arrival is cleared to land.</summary>
            public static event Action<RadioMessage> LandingCleared;

            /// <summary>An arrival goes around: told by the tower, or on its own (weather at minimums).</summary>
            public static event Action<RadioMessage> GoAround;

            /// <summary>An airplane is told to hold (at present position, or in the pattern).</summary>
            public static event Action<RadioMessage> HoldingStarted;

            /// <summary>An arrival has landed and is handed to ground control.</summary>
            public static event Action<RadioMessage> Landed;

            /// <summary>A departure's pushback is approved.</summary>
            public static event Action<RadioMessage> PushbackApproved;

            /// <summary>A departure is cleared to line up and wait.</summary>
            public static event Action<RadioMessage> LineUpCleared;

            /// <summary>A departure is cleared for takeoff.</summary>
            public static event Action<RadioMessage> TakeoffCleared;

            /// <summary>A departure has taken off and is handed to departure control.</summary>
            public static event Action<RadioMessage> TookOff;

            /// <summary>An airport closes; the second entity is what closed it.</summary>
            public static event Action<Entity, Entity> AirportClosed;

            /// <summary>A closed airport opens again.</summary>
            public static event Action<Entity> AirportOpened;

            public static event Action<Entity> GroundDelayStarted;
            public static event Action<Entity> GroundDelayEnded;

            /// <summary>An airport's airspace class changed (by size, the panel, or an add-on), or a new airport got one.</summary>
            public static event Action<Entity, AirspaceClass> AirspaceClassChanged;

            /// <summary>Runways were built, changed or removed (any airport).</summary>
            public static event Action RunwaysChanged;

            internal static bool HasListeners => MessageSent != null || ApproachCleared != null || LandingCleared != null || GoAround != null
                || HoldingStarted != null || Landed != null || PushbackApproved != null || LineUpCleared != null || TakeoffCleared != null
                || TookOff != null || AirportClosed != null || AirportOpened != null || GroundDelayStarted != null || GroundDelayEnded != null;

            internal static void Raise(in AtcMessage message)
            {
                RadioMessage radio = new RadioMessage(message);
                Invoke(MessageSent, radio);
                switch (message.m_Kind)
                {
                    case AtcMessageKind.ClearedApproach:
                        Invoke(ApproachCleared, radio);
                        break;
                    case AtcMessageKind.ClearedToLand:
                        Invoke(LandingCleared, radio);
                        break;
                    case AtcMessageKind.GoAround:
                    case AtcMessageKind.GoingAround:
                        Invoke(GoAround, radio);
                        break;
                    case AtcMessageKind.HoldAtPresentPosition:
                    case AtcMessageKind.HoldInPattern:
                        Invoke(HoldingStarted, radio);
                        break;
                    case AtcMessageKind.ContactGround:
                        Invoke(Landed, radio);
                        break;
                    case AtcMessageKind.PushbackApproved:
                        Invoke(PushbackApproved, radio);
                        break;
                    case AtcMessageKind.LineUpAndWait:
                        Invoke(LineUpCleared, radio);
                        break;
                    case AtcMessageKind.ClearedForTakeoff:
                        Invoke(TakeoffCleared, radio);
                        break;
                    case AtcMessageKind.ContactDeparture:
                        Invoke(TookOff, radio);
                        break;
                    case AtcMessageKind.AirportClosed:
                        Invoke(AirportClosed, message.m_Airport, message.m_Event);
                        break;
                    case AtcMessageKind.AirportOpen:
                        Invoke(AirportOpened, message.m_Airport);
                        break;
                    case AtcMessageKind.GroundDelay:
                        Invoke(GroundDelayStarted, message.m_Airport);
                        break;
                    case AtcMessageKind.GroundDelayEnded:
                        Invoke(GroundDelayEnded, message.m_Airport);
                        break;
                }
            }

            internal static void RaiseAirspaceClassChanged(Entity airport, AirspaceClass airspaceClass)
            {
                if (AirspaceClassChanged == null)
                {
                    return;
                }
                foreach (Action<Entity, AirspaceClass> handler in AirspaceClassChanged.GetInvocationList())
                {
                    try
                    {
                        handler(airport, airspaceClass);
                    }
                    catch (Exception e)
                    {
                        LogHandlerError(handler, e);
                    }
                }
            }

            internal static void RaiseRunwaysChanged()
            {
                if (RunwaysChanged == null)
                {
                    return;
                }
                foreach (Action handler in RunwaysChanged.GetInvocationList())
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception e)
                    {
                        LogHandlerError(handler, e);
                    }
                }
            }

            private static void Invoke(Action<RadioMessage> handlers, RadioMessage message)
            {
                if (handlers == null)
                {
                    return;
                }
                foreach (Action<RadioMessage> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(message);
                    }
                    catch (Exception e)
                    {
                        LogHandlerError(handler, e);
                    }
                }
            }

            private static void Invoke(Action<Entity> handlers, Entity airport)
            {
                if (handlers == null)
                {
                    return;
                }
                foreach (Action<Entity> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(airport);
                    }
                    catch (Exception e)
                    {
                        LogHandlerError(handler, e);
                    }
                }
            }

            private static void Invoke(Action<Entity, Entity> handlers, Entity airport, Entity cause)
            {
                if (handlers == null)
                {
                    return;
                }
                foreach (Action<Entity, Entity> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(airport, cause);
                    }
                    catch (Exception e)
                    {
                        LogHandlerError(handler, e);
                    }
                }
            }
        }

        /// <summary>ATIS: an add-on gives the tower each airport's ATIS, so pilots report "with information X".</summary>
        public static class Atis
        {
            private static readonly List<AtisProvider> s_Providers = new List<AtisProvider>();

            /// <summary>The tower asks once per airport each update, on the main thread; with several providers, the latest registered.</summary>
            public static void RegisterProvider(AtisProvider provider)
            {
                Register(s_Providers, provider);
            }

            public static void UnregisterProvider(AtisProvider provider)
            {
                s_Providers.Remove(provider);
            }

            internal static AtisProvider Current => s_Providers.Count == 0 ? null : s_Providers[s_Providers.Count - 1];
        }

        /// <summary>The player's options (read only).</summary>
        public static class Options
        {
            /// <summary>Add-ons should log per-airplane lines only when this is on.</summary>
            public static bool DetailedLog => Mod.Setting != null && Mod.Setting.DetailedLog;

            /// <summary>The instrument approach procedure option (1.1: "Straight-in final with glide path").</summary>
            public static bool GlidePath => InstrumentApproach;

            public static bool InstrumentApproach => Mod.Setting != null && Mod.Setting.InstrumentApproach;

            public static bool StandardArrival => Mod.Setting != null && Mod.Setting.StandardArrival && Mod.Setting.InstrumentApproach;

            public static bool StandardDeparture => Mod.Setting != null && Mod.Setting.StandardDeparture;

            public static int GlidePathDegrees => Mod.Setting?.GlidePathDegrees ?? 0;

            public static bool ApproachHeightLimits => Mod.Setting != null && Mod.Setting.ApproachHeightLimits;

            /// <summary>The standard instrument departure option (1.1: "Smooth departure turns").</summary>
            public static bool SmoothDepartureTurns => StandardDeparture;

            public static int ArrivalSpacingSeconds => Mod.Setting?.ArrivalSpacingSeconds ?? 0;

            public static int DepartureGapSeconds => Mod.Setting?.DepartureGapSeconds ?? 0;

            public static float FinalLengthNm => Mod.Setting?.FinalLengthNm ?? 0f;

            public static float PatternWidthNm => Mod.Setting?.PatternWidthNm ?? 0f;

            public static float HoldingLegNm => Mod.Setting?.HoldingLegNm ?? 0f;

            public static bool WeatherGoArounds => Mod.Setting != null && Mod.Setting.WeatherGoArounds;

            public static bool Pushback => Mod.Setting != null && Mod.Setting.EnablePushback;

            public static int TaxiSpacingMeters => Mod.Setting?.TaxiSpacing ?? 0;

            public static int HoldShortSetbackMeters => Mod.Setting?.HoldShortSetback ?? 0;
        }
    }
}
