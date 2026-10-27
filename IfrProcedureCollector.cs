using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// The procedures behind the IFR charts, for the map (<see cref="IfrChartOverlaySystem"/>) and the approach chart window
    /// (<see cref="ApproachChartWindowSystem"/>): for every airport of the chart list, its runway ends in use (by name) and
    /// its airways (one per airplane outside connection, designated T1, T2... by bearing from the map centre) with the
    /// SID of each end; and the waypoint names over all of them in list order (approaches first, then the airways'
    /// fixes), so both get the same names whatever is shown.
    /// </summary>
    internal sealed class IfrProcedureCollector
    {
        private readonly WaypointNames m_Names = new WaypointNames();
        private readonly Dictionary<Entity, AirportProcedures> m_Procedures = new Dictionary<Entity, AirportProcedures>();
        private readonly List<AirportProcedures> m_Order = new List<AirportProcedures>();
        private readonly List<float2> m_Connections = new List<float2>();

        /// <summary>The procedures of an airport of the last collection, or null.</summary>
        public AirportProcedures Get(Entity airport) => m_Procedures.TryGetValue(airport, out AirportProcedures procedures) ? procedures : null;

        /// <summary>Collects every airport of <paramref name="charts"/>; returns a signature of everything collected.</summary>
        public ulong Collect(AtcSystem atcSystem, IReadOnlyList<IfrChart> charts, in ChartRadii radii)
        {
            atcSystem.GetAirplaneConnections(m_Connections);
            // Airway designators by bearing from the map centre, so they stay the same after a reload.
            m_Connections.Sort((a, b) => Bearing(a).CompareTo(Bearing(b)));
            m_Procedures.Clear();
            m_Order.Clear();
            ulong signature = Mix(Mix(0x9E3779B97F4A7C15ul, (uint)m_Connections.Count), math.hash(new float3(radii.m_Hold, radii.m_SidTurn, radii.m_SidPlan)));
            for (int i = 0; i < charts.Count; i++)
            {
                Entity airport = charts[i].m_Airport;
                if (!m_Procedures.ContainsKey(airport))
                {
                    AirportProcedures procedures = Collect(atcSystem, airport, radii);
                    m_Procedures.Add(airport, procedures);
                    m_Order.Add(procedures);
                    signature = Sign(signature, procedures);
                }
            }
            return signature;
        }

        /// <summary>Gives the waypoint names over everything collected.</summary>
        public void GiveNames()
        {
            m_Names.Clear();
            for (int p = 0; p < m_Order.Count; p++)
            {
                AirportProcedures procedures = m_Order[p];
                procedures.m_EndNames.Clear();
                for (int i = 0; i < procedures.m_Ends.Count; i++)
                {
                    procedures.m_EndNames.Add(IfrCharts.ApproachNames.Get(m_Names, procedures.m_Ends[i]));
                }
            }
            for (int p = 0; p < m_Order.Count; p++)
            {
                List<AirwayRoute> airways = m_Order[p].m_Airways;
                for (int a = 0; a < airways.Count; a++)
                {
                    airways[a].m_EntryName = m_Names.Get(airways[a].m_Entry);
                    for (int s = 0; s < airways[a].m_Sids.Count; s++)
                    {
                        SidPath sid = airways[a].m_Sids[s];
                        sid.m_FixName = m_Names.Get(sid.m_Fix);
                        airways[a].m_Sids[s] = sid;
                    }
                }
            }
        }

        /// <summary>An airport's runway ends in use (by name), and its airways with the SID of each end towards them.</summary>
        private AirportProcedures Collect(AtcSystem atcSystem, Entity airport, in ChartRadii radii)
        {
            AirportProcedures procedures = new AirportProcedures { m_Airport = airport };
            IReadOnlyList<RunwayInfo> runways = atcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                RunwayInfo runway = runways[i];
                if (runway.Airport != airport)
                {
                    continue;
                }
                byte ends = RunwayNames.UsedEnds(runway.Geometry);
                if ((ends & RunwayGeometry.kAlong) != 0)
                {
                    procedures.m_Ends.Add(atcSystem.GetApproachChart(runway, true, radii.m_Hold));
                }
                if ((ends & RunwayGeometry.kAgainst) != 0)
                {
                    procedures.m_Ends.Add(atcSystem.GetApproachChart(runway, false, radii.m_Hold));
                }
            }
            procedures.m_Ends.Sort((a, b) => string.CompareOrdinal(a.m_Runway, b.m_Runway));
            bool hasCenter = atcSystem.TryGetContactPoint(airport, default, out _, out float2 center);
            if (hasCenter)
            {
                procedures.m_Center = center;
            }
            // The VOR at the airport's centre, on every chart of the airport.
            if (hasCenter && AirportSettings.IsVorEnabled(atcSystem.EntityManager, atcSystem, airport))
            {
                procedures.m_HasVor = true;
                procedures.m_Vor = VorStation.Create(atcSystem.EntityManager, atcSystem.World.GetOrCreateSystemManaged<Game.UI.NameSystem>(), airport, center);
            }
            if (procedures.m_Ends.Count == 0)
            {
                return procedures;
            }
            for (int c = 0; c < m_Connections.Count; c++)
            {
                float2 connection = m_Connections[c];
                if (!atcSystem.TryGetContactPoint(airport, connection, out float2 entry, out _))
                {
                    continue;
                }
                AirwayRoute airway = new AirwayRoute
                {
                    m_Connection = connection,
                    m_Designator = "T" + (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    m_Entry = entry,
                };
                for (int e = 0; e < procedures.m_Ends.Count; e++)
                {
                    bool turns = AtcSystem.GetSid(procedures.m_Ends[e], radii, center, connection, out float2 start, out float2 turnCenter, out float2 finish, out float2 fix);
                    airway.m_Sids.Add(new SidPath { m_Start = start, m_Center = turnCenter, m_Finish = finish, m_Fix = fix, m_Turns = turns });
                }
                procedures.m_Airways.Add(airway);
            }
            return procedures;
        }

        private static ulong Sign(ulong signature, AirportProcedures procedures)
        {
            if (procedures.m_HasVor)
            {
                VorStation vor = procedures.m_Vor;
                signature = Mix(signature, math.hash(vor.m_Position) ^ (uint)(vor.m_Name + vor.m_Ident).GetHashCode());
            }
            return SignProcedures(signature, procedures);
        }

        private static ulong SignProcedures(ulong signature, AirportProcedures procedures)
        {
            for (int i = 0; i < procedures.m_Ends.Count; i++)
            {
                ApproachChartData data = procedures.m_Ends[i];
                signature = Mix(signature, math.hash(data.m_IafLeft) ^ math.hash(data.m_IafRight) * 3u ^ math.hash(data.m_If) * 5u);
                signature = Mix(signature, math.hash(data.m_Faf) ^ math.hash(data.m_Upwind) * 3u ^ math.hash(data.m_Crosswind) * 5u);
                signature = Mix(signature, math.hash(data.m_Downwind) ^ math.hash(data.m_Threshold) * 3u ^ math.hash(data.m_RunwayEnd) * 5u);
                signature = Mix(signature, math.hash(new float4(data.m_HoldLeft, data.m_HoldRight, data.m_HoldStraight, data.m_HoldLeg)));
                signature = Mix(signature, math.hash(new float3(data.m_FloorInitialLeft, data.m_FloorInitialRight, data.m_FloorIntermediate)));
            }
            for (int a = 0; a < procedures.m_Airways.Count; a++)
            {
                signature = Mix(signature, math.hash(new float4(procedures.m_Airways[a].m_Connection, procedures.m_Airways[a].m_Entry)));
            }
            return signature;
        }

        private static float Bearing(float2 position) => (RunwayNames.Heading(position) + 360f) % 360f;

        internal static ulong Mix(ulong signature, uint value) => (signature ^ value) * 0x100000001B3ul + 0x9E3779B97F4A7C15ul;
    }
}
