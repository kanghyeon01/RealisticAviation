using Game.Vehicles;
using Unity.Entities;

namespace RealisticAviation
{
    public partial class AtcSystem
    {
        private partial struct AtcJob
        {
            // ------------------------------------------------------------ what add-ons set through the public Api

            /// <summary>The operating values at an airport: the options, with an add-on's overrides on top.</summary>
            private AtcSettings SettingsAt(Entity airport)
            {
                return m_ApiOverrides.TryGetValue(airport, out OperatingOverride overrides) ? overrides.Apply(m_Settings) : m_Settings;
            }

            /// <summary>The operating values at the airport of a runway.</summary>
            private AtcSettings SettingsForRunway(Entity runway)
            {
                return m_Runways.TryGetValue(runway, out RunwayState state) ? SettingsAt(state.m_Airport) : m_Settings;
            }

            /// <summary>
            /// Sends around the arrivals add-ons asked for, when they are on final: cleared, in the air on their runway's lane
            /// and not yet down. Others are left alone (the order is dropped).
            /// </summary>
            private void ApplyApiGoArounds()
            {
                for (int i = 0; i < m_ApiGoArounds.Length; i++)
                {
                    ApiGoAround order = m_ApiGoArounds[i];
                    Entity entity = order.m_Airplane;
                    if (!m_Flights.TryGetValue(entity, out Flight flight) || !m_CurrentLaneData.TryGetComponent(entity, out AircraftCurrentLane lane))
                    {
                        continue;
                    }
                    if (!flight.m_Arrival || flight.m_Clearance != Clearance.Cleared || flight.m_TouchedDown || flight.m_InCircuit || flight.m_Holding
                        || (lane.m_LaneFlags & AircraftLaneFlags.Flying) == 0 || RunwayOfLane(lane.m_Lane) != flight.m_Runway)
                    {
                        LogEvent(entity, "is not on final; an add-on's go-around order is dropped for runway", flight.m_Runway);
                        continue;
                    }
                    GoAround(entity, ref flight, "add-on: " + order.m_Reason, false);
                }
                m_ApiGoArounds.Clear();
            }
        }
    }
}
