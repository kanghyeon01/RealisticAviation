using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>One radio transmission (a tower instruction and its readback, or a broadcast to all aircraft).</summary>
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

        /// <summary>What closed the airport (a disaster event, or the airport itself) for <see cref="AtcMessageKind.AirportClosed"/>; else usually Entity.Null.</summary>
        public Entity Event => m_Message.m_Event;

        /// <summary>The simulation frame it was said in.</summary>
        public uint Frame => m_Message.m_Frame;

        public bool IsBroadcast => m_Message.m_Airplane == Entity.Null;
    }

    /// <summary>
    /// The public interface of Realistic Aviation for add-ons. Everything here is for the main thread only (a system's
    /// OnUpdate, a UI callback). Other public types of this assembly are not part of it and may change in any version.
    /// </summary>
    public static class Api
    {
        /// <summary>
        /// The version of this interface: raised when something is added, so an add-on can check for what it needs. A property,
        /// not a constant, so an add-on reads the version of the Realistic Aviation that is running, not the one it was built with.
        /// </summary>
        public static int Version => 1;

        /// <summary>Gives an airport's ATIS to the tower, for "with information X"; see <see cref="RegisterAtisProvider"/>.</summary>
        public delegate bool AtisProvider(Entity airport, out AtisState state);

        /// <summary>Every radio transmission, in order, once per tower update (every 16 simulation frames).</summary>
        public static event Action<RadioMessage> MessageSent;

        /// <summary>An airplane goes around: told by the tower, or on its own (weather at minimums).</summary>
        public static event Action<RadioMessage> GoAround;

        /// <summary>An airport closes; the second entity is what closed it (a disaster event, or the airport itself).</summary>
        public static event Action<Entity, Entity> AirportClosed;

        /// <summary>A closed airport opens again.</summary>
        public static event Action<Entity> AirportOpened;

        private static readonly List<AtisProvider> s_AtisProviders = new List<AtisProvider>();

        internal static AtcSystem Tower { get; set; }

        /// <summary>The tower is running (a city is loaded or loading). The other members return nothing until then.</summary>
        public static bool IsReady => Tower != null;

        /// <summary>The "Detailed log" option: add-ons should log per-airplane lines only when it is on.</summary>
        public static bool DetailedLog => Mod.Setting != null && Mod.Setting.DetailedLog;

        // ---- Airports

        /// <summary>Every airport in the city, once each: top-level buildings with airplane stops, outside connections excluded.</summary>
        public static void GetAirports(List<Entity> airports)
        {
            airports.Clear();
            if (Tower == null)
            {
                return;
            }
            NativeList<Entity> found = new NativeList<Entity>(4, Allocator.Temp);
            try
            {
                Airports.Collect(Tower.EntityManager, Tower.AirplaneStopQuery, found);
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

        /// <summary>The airport an entity belongs to (the airport itself, or anything it owns, such as a runway), or Entity.Null.</summary>
        public static Entity GetAirportOf(Entity entity)
        {
            return Tower == null ? Entity.Null : Airports.GetAirport(Tower.EntityManager, Tower.AirplaneStopQuery, entity);
        }

        /// <summary>The airport's airspace class, or false if it has none yet (not an airport, or its runways not found yet).</summary>
        public static bool TryGetAirspaceClass(Entity airport, out AirspaceClass airspaceClass)
        {
            if (Tower == null)
            {
                airspaceClass = default;
                return false;
            }
            return Tower.TryGetAirspaceClass(airport, out airspaceClass);
        }

        /// <summary>The runway ends in use for landing, named as on the radio ("27", "09L"), sorted, added to <paramref name="names"/>.</summary>
        public static void GetLandingRunways(Entity airport, List<string> names)
        {
            Tower?.GetLandingRunways(airport, names);
        }

        /// <summary>Whether the tower has closed the airport, and what closed it (a disaster event, or the airport itself).</summary>
        public static bool IsClosed(Entity airport, out Entity closedBy)
        {
            if (Tower == null)
            {
                closedBy = Entity.Null;
                return false;
            }
            return Tower.IsClosed(airport, out closedBy);
        }

        /// <summary>The airport's ICAO code: the one the player entered, or else one made from its name.</summary>
        public static string GetAirportCode(Entity airport)
        {
            return Tower == null ? null : AirportCodes.Get(Tower.EntityManager, Tower.NameSystem, airport);
        }

        // ---- Radio

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
        /// The words of a transmission as the info panels show them: the controller's and the pilot's (empty for a broadcast);
        /// <paramref name="pilotFirst"/> when the pilot called first (a first contact, a check-in).
        /// </summary>
        public static void GetRadioText(in RadioMessage message, out string controller, out string pilot, out bool pilotFirst)
        {
            if (Tower == null)
            {
                controller = string.Empty;
                pilot = string.Empty;
                pilotFirst = false;
                return;
            }
            Tower.Radio.Describe(Tower.EntityManager, Tower.NameSystem, Tower.PrefabSystem, Tower.RunwayList, message.m_Message,
                message.Frequency, out controller, out pilot, out pilotFirst);
        }

        // ---- ATIS

        /// <summary>
        /// Gives the tower each airport's ATIS. The tower asks once per airport each update, on the main thread. With several
        /// providers registered, the latest one is used. Unregister it when your system is destroyed.
        /// </summary>
        public static void RegisterAtisProvider(AtisProvider provider)
        {
            if (provider != null)
            {
                s_AtisProviders.Remove(provider);
                s_AtisProviders.Add(provider);
            }
        }

        public static void UnregisterAtisProvider(AtisProvider provider)
        {
            s_AtisProviders.Remove(provider);
        }

        internal static AtisProvider CurrentAtisProvider => s_AtisProviders.Count == 0 ? null : s_AtisProviders[s_AtisProviders.Count - 1];

        // ---- Raised by the tower

        internal static bool HasListeners => MessageSent != null || GoAround != null || AirportClosed != null || AirportOpened != null;

        internal static void Raise(in AtcMessage message)
        {
            RadioMessage radio = new RadioMessage(message);
            Invoke(MessageSent, radio);
            switch (message.m_Kind)
            {
                case AtcMessageKind.GoAround:
                case AtcMessageKind.GoingAround:
                    Invoke(GoAround, radio);
                    break;
                case AtcMessageKind.AirportClosed:
                    if (AirportClosed != null)
                    {
                        foreach (Action<Entity, Entity> handler in AirportClosed.GetInvocationList())
                        {
                            try
                            {
                                handler(message.m_Airport, message.m_Event);
                            }
                            catch (Exception e)
                            {
                                LogHandlerError(handler, e);
                            }
                        }
                    }
                    break;
                case AtcMessageKind.AirportOpen:
                    if (AirportOpened != null)
                    {
                        foreach (Action<Entity> handler in AirportOpened.GetInvocationList())
                        {
                            try
                            {
                                handler(message.m_Airport);
                            }
                            catch (Exception e)
                            {
                                LogHandlerError(handler, e);
                            }
                        }
                    }
                    break;
            }
        }

        /// <summary>Calls each handler on its own, so one failing add-on neither stops the others nor the tower.</summary>
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

        private static void LogHandlerError(Delegate handler, Exception e)
        {
            Mod.Log.Error(e, $"An add-on's event handler failed: {handler.Method.DeclaringType?.FullName}.{handler.Method.Name}");
        }
    }
}
