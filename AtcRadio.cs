using System.Collections.Generic;
using System.Globalization;
using Colossal.Entities;
using Colossal.Localization;
using Game.Prefabs;
using Game.Routes;
using Game.SceneFlow;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>What the tower said to an airplane (phase 4-2); the words are built on the main thread when shown.</summary>
    public enum AtcMessageKind : byte
    {
        HoldShort,
        HoldShortForArrival,
        HoldShortClosed,
        LineUpAndWait,
        ClearedForTakeoff,
        CrossRunway,
        HoldAtPresentPosition,
        ClearedToLand,
        GoAround,

        /// <summary>The pilot's own go-around (weather at minimums): the pilot calls first, the tower answers.</summary>
        GoingAround,
        ExtendDownwind,

        /// <summary>The extended downwind is used up and the runway still isn't free: the tower holds the airplane there.</summary>
        HoldInPattern,
        LandingClearanceCancelled,
        ClearedApproach,
        ApproachClearanceCancelled,
        PushbackApproved,
        TaxiToRunway,
        TaxiToGate,

        /// <summary>Taxiing in to a gate another airplane still uses: wait clear of it until it is free.</summary>
        HoldForGate,

        // To all aircraft of an airport (no airplane, no readback).
        AirportClosed,
        AirportOpen,
        GroundDelay,
        GroundDelayEnded,
    }

    /// <summary>
    /// The airport frequency a message is said on: approach control for arrivals until they are cleared for the approach,
    /// ground control for pushback and taxi, the tower for the runway.
    /// </summary>
    public enum AtcFrequency : byte
    {
        Tower,
        Ground,
        Approach,
    }

    /// <summary>One tower transmission, as recorded by the tower job.</summary>
    public struct AtcMessage
    {
        /// <summary>The airplane spoken to, or Entity.Null for a broadcast to all aircraft.</summary>
        public Entity m_Airplane;
        public Entity m_Runway;
        public Entity m_Airport;

        /// <summary>Direction of travel on the runway (landing or takeoff), for the runway designator.</summary>
        public float2 m_Direction;

        /// <summary>The disaster event for closure messages, else Entity.Null.</summary>
        public Entity m_Event;

        /// <summary>Altitude in feet (hold), position in the landing sequence (cleared to land), or 1 for right-hand traffic (go-around).</summary>
        public int m_Value;

        public uint m_Frame;
        public AtcMessageKind m_Kind;

        /// <summary>Broadcasts (no airplane) are heard on every frequency of the airport.</summary>
        public AtcFrequency m_Frequency;

        public static AtcFrequency FrequencyOf(AtcMessageKind kind)
        {
            switch (kind)
            {
                case AtcMessageKind.PushbackApproved:
                case AtcMessageKind.TaxiToRunway:
                case AtcMessageKind.TaxiToGate:
                case AtcMessageKind.HoldForGate:
                    return AtcFrequency.Ground;
                case AtcMessageKind.HoldAtPresentPosition:
                case AtcMessageKind.ClearedApproach:
                case AtcMessageKind.ApproachClearanceCancelled:
                    return AtcFrequency.Approach;
                default:
                    return AtcFrequency.Tower;
            }
        }
    }

    /// <summary>
    /// Turns tower messages into radio text on the main thread: the callsign, the tower's instruction in standard (FAA)
    /// English phraseology, and the pilot's readback. Callsign = route name (spaces removed) + the airplane's number on
    /// that route; the game has no such number, so one is assigned here (the lowest free one, kept while the airplane
    /// stays on the route). Airplanes without a route are "TRAFFIC" + a number from their entity.
    /// </summary>
    internal sealed class AtcRadio
    {
        private readonly Dictionary<Entity, Dictionary<Entity, int>> m_RouteNumbers = new Dictionary<Entity, Dictionary<Entity, int>>();
        private readonly HashSet<int> m_UsedNumbers = new HashSet<int>();
        private readonly List<Entity> m_Stale = new List<Entity>();

        public string GetCallsign(EntityManager entityManager, NameSystem nameSystem, PrefabSystem prefabSystem, Entity airplane)
        {
            if (!entityManager.TryGetComponent(airplane, out CurrentRoute currentRoute) || !entityManager.Exists(currentRoute.m_Route))
            {
                return "TRAFFIC " + (airplane.Index % 1000).ToString("000");
            }
            Entity route = currentRoute.m_Route;
            string name = GetRouteName(entityManager, nameSystem, prefabSystem, route).Replace(" ", string.Empty);
            return name + GetNumber(entityManager, route, airplane).ToString("00");
        }

        private static string GetRouteName(EntityManager entityManager, NameSystem nameSystem, PrefabSystem prefabSystem, Entity route)
        {
            if (nameSystem.TryGetCustomName(route, out string customName))
            {
                return customName;
            }
            string number = entityManager.TryGetComponent(route, out RouteNumber routeNumber) ? routeNumber.m_Number.ToString() : string.Empty;
            if (entityManager.TryGetComponent(route, out PrefabRef prefabRef) && prefabSystem.TryGetPrefab(prefabRef.m_Prefab, out RoutePrefab prefab))
            {
                LocalizationDictionary dictionary = GameManager.instance.localizationManager.activeDictionary;
                if (dictionary.TryGetValue(prefab.m_LocaleID + "[" + prefab.name + "]", out string format))
                {
                    return format.Replace("{NUMBER}", number);
                }
            }
            return "LINE" + number;
        }

        /// <summary>The airplane's number on its route: kept once given; a new airplane gets the lowest number not in use.</summary>
        private int GetNumber(EntityManager entityManager, Entity route, Entity airplane)
        {
            if (!m_RouteNumbers.TryGetValue(route, out Dictionary<Entity, int> numbers))
            {
                numbers = new Dictionary<Entity, int>();
                m_RouteNumbers.Add(route, numbers);
            }
            if (numbers.TryGetValue(airplane, out int number))
            {
                return number;
            }
            // Numbers of airplanes that left the route (or no longer exist) become free again.
            m_Stale.Clear();
            foreach (KeyValuePair<Entity, int> pair in numbers)
            {
                if (!entityManager.TryGetComponent(pair.Key, out CurrentRoute other) || other.m_Route != route)
                {
                    m_Stale.Add(pair.Key);
                }
            }
            for (int i = 0; i < m_Stale.Count; i++)
            {
                numbers.Remove(m_Stale[i]);
            }
            m_UsedNumbers.Clear();
            foreach (int used in numbers.Values)
            {
                m_UsedNumbers.Add(used);
            }
            number = 1;
            while (m_UsedNumbers.Contains(number))
            {
                number++;
            }
            numbers.Add(airplane, number);
            return number;
        }

        /// <summary>The frequency as labelled in the panel, e.g. "TWR 118.300".</summary>
        public static string FrequencyLabel(Entity airport, AtcFrequency frequency)
        {
            string name = frequency == AtcFrequency.Ground ? "GND" : frequency == AtcFrequency.Approach ? "APP" : "TWR";
            return name + " " + FrequencyNumber(airport, frequency);
        }

        /// <summary>The controller's name on the radio: "Ground", "Tower" or "Approach".</summary>
        private static string FacilityName(AtcFrequency frequency)
        {
            return frequency == AtcFrequency.Ground ? "Ground" : frequency == AtcFrequency.Approach ? "Approach" : "Tower";
        }

        /// <summary>
        /// The airport's frequency in MHz, e.g. "118.300": made up from the airport entity so that it stays the same while
        /// playing, in the usual VHF ranges (tower 118.000–121.400, ground 121.600–121.900, approach 124.000–127.975;
        /// 25 kHz steps).
        /// </summary>
        public static string FrequencyNumber(Entity airport, AtcFrequency frequency)
        {
            uint hash = math.hash(new int2(airport.Index, airport.Version));
            float megahertz = frequency == AtcFrequency.Ground ? 121.6f + hash % 13u * 0.025f
                : frequency == AtcFrequency.Approach ? 124f + (hash >> 8) % 160u * 0.025f
                : 118f + hash % 137u * 0.025f;
            return megahertz.ToString("F3", CultureInfo.InvariantCulture);
        }

        /// <summary>Clears the numbering (a different city was loaded).</summary>
        public void Reset()
        {
            m_RouteNumbers.Clear();
        }

        /// <summary>
        /// The radio text of one message, as transcript lines ("ATC: ..." and "&lt;callsign&gt;: ..."), with the callsign, runway
        /// designator and event name filled in.
        /// </summary>
        public void Describe(EntityManager entityManager, NameSystem nameSystem, PrefabSystem prefabSystem, IReadOnlyList<RunwayInfo> runways,
            in AtcMessage message, AtcFrequency frequency, out string tower, out string pilot, out bool pilotFirst)
        {
            pilotFirst = false;
            string eventName = GetEventName(entityManager, prefabSystem, message.m_Event);
            if (message.m_Airplane == Entity.Null)
            {
                // FAA all-stations broadcast: "Attention all aircraft, <facility>, <message>." Nobody reads it back. It is heard on
                // every frequency, each controller naming its own facility (Portland Ground, Portland Tower, ...).
                string station = nameSystem.GetRenderedLabelName(message.m_Airport) + " " + FacilityName(frequency);
                string news = message.m_Kind == AtcMessageKind.AirportClosed ? ClosedPhrase(eventName)
                    : message.m_Kind == AtcMessageKind.GroundDelay ? "ground delay in effect, arrivals expect holding"
                    : message.m_Kind == AtcMessageKind.GroundDelayEnded ? "ground delay ended"
                    : "airport open";
                tower = $"ATC: Attention all aircraft, {station}, {news}.";
                pilot = string.Empty;
                return;
            }
            string callsign = GetCallsign(entityManager, nameSystem, prefabSystem, message.m_Airplane);
            string runway = message.m_Runway != Entity.Null ? RunwayNames.Name(runways, message.m_Runway, message.m_Direction) : string.Empty;
            Phrase(message, callsign, runway, eventName, FrequencyNumber(message.m_Airport, AtcFrequency.Tower), out tower, out pilot);
            // As written in a radio transcript: who is speaking, then what was said.
            tower = "ATC: " + tower;
            pilot = callsign + ": " + pilot;
            if (message.m_Kind == AtcMessageKind.GoingAround)
            {
                // The pilot speaks first; "tower" and "pilot" are then the first and second line.
                pilotFirst = true;
                (tower, pilot) = (pilot, tower);
            }
        }

        /// <summary>The disaster's display name in the game's language, or empty.</summary>
        private static string GetEventName(EntityManager entityManager, PrefabSystem prefabSystem, Entity hazard)
        {
            if (hazard == Entity.Null || !entityManager.TryGetComponent(hazard, out PrefabRef prefabRef))
            {
                return string.Empty;
            }
            string name = prefabSystem.GetPrefabName(prefabRef.m_Prefab);
            return GameManager.instance.localizationManager.activeDictionary.TryGetValue("Assets.NAME[" + name + "]", out string localized) ? localized : name;
        }

        /// <summary>The tower's words and the pilot's readback for one message.</summary>
        public static void Phrase(in AtcMessage message, string callsign, string runway, string eventName, string towerFrequency,
            out string tower, out string pilot)
        {
            string closed = ClosedPhrase(eventName);
            switch (message.m_Kind)
            {
                case AtcMessageKind.HoldShort:
                    tower = $"{callsign}, hold short of runway {runway}.";
                    pilot = $"Hold short of runway {runway}, {callsign}.";
                    break;
                case AtcMessageKind.HoldShortForArrival:
                    tower = $"{callsign}, hold short of runway {runway}, traffic landing.";
                    pilot = $"Holding short of runway {runway}, {callsign}.";
                    break;
                case AtcMessageKind.HoldShortClosed:
                    tower = $"{callsign}, hold short of runway {runway}, {closed}.";
                    pilot = $"Holding short of runway {runway}, {callsign}.";
                    break;
                case AtcMessageKind.LineUpAndWait:
                    tower = $"{callsign}, runway {runway}, line up and wait.";
                    pilot = $"Runway {runway}, line up and wait, {callsign}.";
                    break;
                case AtcMessageKind.ClearedForTakeoff:
                    tower = $"{callsign}, runway {runway}, cleared for takeoff.";
                    pilot = $"Runway {runway}, cleared for takeoff, {callsign}.";
                    break;
                case AtcMessageKind.CrossRunway:
                    tower = $"{callsign}, cross runway {runway}.";
                    pilot = $"Crossing runway {runway}, {callsign}.";
                    break;
                case AtcMessageKind.HoldAtPresentPosition:
                    tower = $"{callsign}, hold at present position, maintain {message.m_Value:N0}, expect further clearance.";
                    pilot = $"Hold at present position, maintain {message.m_Value:N0}, {callsign}.";
                    break;
                case AtcMessageKind.ClearedApproach:
                    string number = message.m_Value > 1 ? $"number {NumberWord(message.m_Value)}, " : string.Empty;
                    tower = $"{callsign}, {number}cleared ILS runway {runway} approach, contact tower {towerFrequency}.";
                    pilot = $"Cleared ILS runway {runway} approach, contact tower {towerFrequency}, {callsign}.";
                    break;
                case AtcMessageKind.ApproachClearanceCancelled:
                    tower = $"{callsign}, {closed}, cancel approach clearance, hold at present position.";
                    pilot = $"Approach clearance cancelled, holding, {callsign}.";
                    break;
                case AtcMessageKind.ClearedToLand:
                    tower = $"{callsign}, runway {runway}, cleared to land.";
                    pilot = $"Runway {runway}, cleared to land, {callsign}.";
                    break;
                case AtcMessageKind.GoAround:
                    string side = message.m_Value != 0 ? "right" : "left";
                    // The controller gives the reason, which is why the tower (not the pilot) calls it: traffic on the runway, or the
                    // closure.
                    string reason = message.m_Event != Entity.Null ? closed : "traffic on the runway";
                    tower = $"{callsign}, go around, {reason}, climb and maintain 1,500, make {side} traffic runway {runway}.";
                    pilot = $"Going around, {side} traffic runway {runway}, {callsign}.";
                    break;
                case AtcMessageKind.GoingAround:
                    string traffic = message.m_Value != 0 ? "right" : "left";
                    tower = $"{callsign}, roger, climb and maintain 1,500, make {traffic} traffic runway {runway}.";
                    pilot = $"Tower, {callsign}, going around.";
                    break;
                case AtcMessageKind.HoldInPattern:
                    tower = $"{callsign}, hold at present position, maintain 1,500, expect further clearance.";
                    pilot = $"Hold at present position, maintain 1,500, {callsign}.";
                    break;
                case AtcMessageKind.ExtendDownwind:
                    tower = $"{callsign}, extend downwind, I'll call your base.";
                    pilot = $"Extend downwind, {callsign}.";
                    break;
                case AtcMessageKind.LandingClearanceCancelled:
                    tower = $"{callsign}, {closed}, cancel landing clearance, hold at present position.";
                    pilot = $"Landing clearance cancelled, holding, {callsign}.";
                    break;
                case AtcMessageKind.PushbackApproved:
                    tower = $"{callsign}, pushback approved.";
                    pilot = $"Pushback approved, {callsign}.";
                    break;
                case AtcMessageKind.TaxiToRunway:
                    if (string.IsNullOrEmpty(runway))
                    {
                        tower = $"{callsign}, taxi to the runway.";
                        pilot = $"Taxi to the runway, {callsign}.";
                    }
                    else
                    {
                        tower = $"{callsign}, runway {runway}, taxi.";
                        pilot = $"Runway {runway}, taxi, {callsign}.";
                    }
                    break;
                case AtcMessageKind.HoldForGate:
                    tower = $"{callsign}, hold position, gate occupied.";
                    pilot = $"Hold position, {callsign}.";
                    break;
                case AtcMessageKind.TaxiToGate:
                    tower = $"{callsign}, taxi to the gate.";
                    pilot = $"Taxi to the gate, {callsign}.";
                    break;
                default:
                    tower = pilot = string.Empty;
                    break;
            }
        }

        private static string ClosedPhrase(string eventName)
        {
            return string.IsNullOrEmpty(eventName) ? "airport closed" : "airport closed due to " + eventName.ToLowerInvariant();
        }

        private static string NumberWord(int number)
        {
            switch (number)
            {
                case 2: return "two";
                case 3: return "three";
                case 4: return "four";
                case 5: return "five";
                case 6: return "six";
                default: return number.ToString();
            }
        }
    }
}
