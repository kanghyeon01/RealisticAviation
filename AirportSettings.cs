using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// The airport settings that are saved with the city (line up and wait, long pushback, ground delay, code, pattern side), read and written
    /// the same way for the airport info panel and for add-ons (Api). Main thread only.
    /// </summary>
    internal static class AirportSettings
    {
        /// <summary>The airport's present airspace class (D without airspace data, as the tower assumes).</summary>
        public static AirspaceClass CurrentClass(AtcSystem atcSystem, Entity airport)
        {
            return atcSystem.TryGetAirspace(airport, out AirportSpace space) ? space.m_Class : AirspaceClass.D;
        }

        public static bool IsLineUpEnabled(EntityManager entityManager, AtcSystem atcSystem, Entity airport)
        {
            bool hasSetting = entityManager.HasComponent<LineUpSetting>(airport);
            LineUpSetting setting = hasSetting ? entityManager.GetComponentData<LineUpSetting>(airport) : default;
            return LineUpSetting.IsEnabled(hasSetting, setting, CurrentClass(atcSystem, airport));
        }

        /// <summary>Sets line up and wait as a choice for the airport's present class (a class change brings back the default).</summary>
        public static void SetLineUp(EntityManager entityManager, AtcSystem atcSystem, Entity airport, bool enable)
        {
            Set(entityManager, airport, new LineUpSetting { m_Enabled = enable, m_Class = (byte)CurrentClass(atcSystem, airport) });
            Mod.Log.Info($"Line up and wait {(enable ? "enabled" : "disabled")} at airport {airport}");
        }

        public static bool IsLongPushback(EntityManager entityManager, Entity airport)
        {
            return entityManager.HasComponent<LongPushbackAirport>(airport);
        }

        public static void SetLongPushback(EntityManager entityManager, Entity airport, bool enable)
        {
            if (enable && !entityManager.HasComponent<LongPushbackAirport>(airport))
            {
                entityManager.AddComponent<LongPushbackAirport>(airport);
            }
            else if (!enable && entityManager.HasComponent<LongPushbackAirport>(airport))
            {
                entityManager.RemoveComponent<LongPushbackAirport>(airport);
            }
            Mod.Log.Info($"Long pushback {(enable ? "enabled" : "disabled")} at airport {airport}");
        }

        public static GroundDelaySetting GetGroundDelay(EntityManager entityManager, AtcSystem atcSystem, Entity airport)
        {
            return entityManager.HasComponent<GroundDelaySetting>(airport)
                ? entityManager.GetComponentData<GroundDelaySetting>(airport)
                : GroundDelaySetting.Default(atcSystem.GetGateCount(airport));
        }

        public static void SetGroundDelay(EntityManager entityManager, Entity airport, GroundDelaySetting setting)
        {
            setting.m_Limit = (byte)math.clamp((int)setting.m_Limit, GroundDelaySetting.kMinLimit, GroundDelaySetting.kMaxLimit);
            Set(entityManager, airport, setting);
            Mod.Log.Info($"Ground delay at airport {airport}: {(setting.m_Enabled ? "on" : "off")}, limit {setting.m_Limit}");
        }

        /// <summary>
        /// Sets the airport's code: 3–4 letters or digits; empty (or null) brings back the automatic code. Returns false, and
        /// changes nothing, for anything else.
        /// </summary>
        public static bool SetAirportCode(EntityManager entityManager, Entity airport, string text)
        {
            text = text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                if (entityManager.HasComponent<AirportCode>(airport))
                {
                    entityManager.RemoveComponent<AirportCode>(airport);
                }
                Mod.Log.Info($"Airport {airport} uses its automatic code");
                return true;
            }
            if (!AirportCode.TryCreate(text, out AirportCode code))
            {
                return false;
            }
            Set(entityManager, airport, code);
            Mod.Log.Info($"Airport {airport} code set to {code}");
            return true;
        }

        /// <summary>The player's (or an add-on's) choice for this runway end, or else the default the tower uses.</summary>
        public static bool IsRightHand(EntityManager entityManager, AtcSystem atcSystem, RunwayInfo runway, float2 landing)
        {
            for (int i = 0; i < runway.Edges.Count; i++)
            {
                Entity edge = runway.Edges[i];
                if (entityManager.Exists(edge) && entityManager.HasComponent<RunwayPattern>(edge) && entityManager.HasComponent<Curve>(edge)
                    && entityManager.GetComponentData<RunwayPattern>(edge).TryGetRightHand(EdgeDirection(entityManager, edge), landing, out bool rightHand))
                {
                    return rightHand;
                }
            }
            return IsRightHandByDefault(atcSystem, runway, landing);
        }

        /// <summary>Right-hand when a parallel runway lies to the left (so the pattern stays outside), or else a neighbouring airport.</summary>
        private static bool IsRightHandByDefault(AtcSystem atcSystem, RunwayInfo runway, float2 landing)
        {
            float width = Mod.Setting.PatternWidthNm * Setting.kMetersPerNauticalMile;
            float2 left = MathUtils.Left(landing);
            float2 right = -left;
            bool parallelRight = false;
            IReadOnlyList<RunwayInfo> runways = atcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                if (runways[i] == runway)
                {
                    continue;
                }
                if (RunwayGeometry.IsParallelOnSide(runway.Geometry, runways[i].Geometry, landing, left, width))
                {
                    return true;
                }
                parallelRight |= RunwayGeometry.IsParallelOnSide(runway.Geometry, runways[i].Geometry, landing, right, width);
            }
            // No parallel runway: away from a neighbouring airport on the left (the same rule as the tower's).
            return !parallelRight && atcSystem.IsNeighbourOnLeft(runway.Airport, runway.Geometry.Middle, landing);
        }

        /// <summary>Sets one runway end's pattern side, on every edge of that runway.</summary>
        public static void SetRightHand(EntityManager entityManager, RunwayInfo runway, float2 landing, bool rightHand)
        {
            for (int i = 0; i < runway.Edges.Count; i++)
            {
                Entity edge = runway.Edges[i];
                if (!entityManager.Exists(edge) || !entityManager.HasComponent<Curve>(edge))
                {
                    continue;
                }
                RunwayPattern pattern = entityManager.HasComponent<RunwayPattern>(edge) ? entityManager.GetComponentData<RunwayPattern>(edge) : default;
                pattern.SetRightHand(EdgeDirection(entityManager, edge), landing, rightHand);
                Set(entityManager, edge, pattern);
            }
        }

        private static float2 EdgeDirection(EntityManager entityManager, Entity edge)
        {
            Bezier4x3 bezier = entityManager.GetComponentData<Curve>(edge).m_Bezier;
            return bezier.d.xz - bezier.a.xz;
        }

        private static void Set<T>(EntityManager entityManager, Entity entity, T data) where T : unmanaged, IComponentData
        {
            if (entityManager.HasComponent<T>(entity))
            {
                entityManager.SetComponentData(entity, data);
            }
            else
            {
                entityManager.AddComponentData(entity, data);
            }
        }
    }
}
