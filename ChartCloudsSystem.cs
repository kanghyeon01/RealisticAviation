using System.Collections.Generic;
using Game;
using Game.Tools;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace RealisticAviation
{
    /// <summary>
    /// Turns the clouds, the sun's shadows, the snow on the ground and falling rain or snow off while the VFR or the IFR
    /// chart is open (user, 2026-10-26: they veil, shade and discolour the chart; the game has no such option). The game's
    /// own settings switch the HDRP volumetric clouds and the distance cloud layer on a volume (CloudsQualitySettings) and
    /// the shadows on the sun light (ShadowsQualitySettings); the snow shows as much as a global shader vector says
    /// (colossal_SnowScale, set by SnowSystem). This keeps a global volume of its own, of a higher priority, that switches
    /// the clouds off, turns the sun's shadows off and holds the snow scale where the game's "snow visuals" setting off puts
    /// it, only while a chart is open; on closing, the shadows and the snow come back as they were. The light is fixed too, as
    /// the game's "day/night visuals" setting off fixes it (PlanetarySystem: 14:30 on day 177), so the chart's colours can be
    /// matched to the paper chart's once and look the same at any hour (user: snow off alone wasn't enough); the game's
    /// clock takes over again on closing. The same volume turns the tonemapping off, so the colours come out as given.
    /// </summary>
    public partial class ChartCloudsSystem : GameSystemBase
    {
        private const float kPriority = 1000f;
        private static readonly int kSnowScale = Shader.PropertyToID("colossal_SnowScale");

        /// <summary>The scale the game sets with its "snow visuals" off (SnowSystem: x 0 instead of 8; the rest stays 1).</summary>
        private static readonly Vector4 kNoSnow = new Vector4(0f, 1f, 1f, 1f);

        private ToolSystem m_ToolSystem;
        private GameObject m_Object;
        private Volume m_Volume;
        private Exposure m_Exposure;

        /// <summary>
        /// The chart's exposure above the one that shows a white surface lit by the sun alone as white (EV100 = log2(sun lux
        /// / (pi x 1.2)), HDRP's exposure scale): the sky's light comes on top.
        /// </summary>
        private const float kExposureBias = 0f;
        private HDAdditionalLightData m_Sun;
        private bool m_SunShadows;
        private Vector4 m_SnowScale;
        private GameObject m_Precipitation;
        private bool m_PrecipitationActive;
        private bool m_Chart;
        private Game.Simulation.PlanetarySystem m_Planetary;
        private bool m_OverrideTime;
        private float m_Time;
        private int m_Day;

        // The light of the game's "day/night visuals" off.
        private const float kChartTime = 14.5f;
        private const int kChartDay = 177;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_Planetary = World.GetOrCreateSystemManaged<Game.Simulation.PlanetarySystem>();
            m_Object = new GameObject("RealisticAviationChartClouds");
            Object.DontDestroyOnLoad(m_Object);
            m_Volume = m_Object.AddComponent<Volume>();
            m_Volume.isGlobal = true;
            m_Volume.priority = kPriority;
            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.Add<VolumetricClouds>().enable.Override(false);
            profile.Add<VisualEnvironment>().cloudType.Override(0);
            // No tonemapping: it pressed the bright tints toward white and took their colour (a saturated yellow came out
            // greenish), so no gain could match the paper chart's colours; without it the screen colour follows the tint.
            profile.Add<Tonemapping>().mode.Override(TonemappingMode.None);
            // A fixed exposure, set from the sun when a chart opens: the automatic one followed what was on screen (more water,
            // brighter), so the same tint came out brighter or darker as the view moved and no gain held.
            m_Exposure = profile.Add<Exposure>();
            m_Exposure.mode.Override(ExposureMode.Fixed);
            m_Exposure.mode.overrideState = false;
            m_Volume.sharedProfile = profile;
            m_Volume.enabled = false;
            RenderPipelineManager.beginContextRendering += OnBeginRendering;
        }

        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginRendering;
            if (m_Object != null)
            {
                Object.Destroy(m_Volume.sharedProfile);
                Object.Destroy(m_Object);
            }
            base.OnDestroy();
        }

        /// <summary>
        /// Right before each frame renders: the snow scale set again while a chart is open. Set only in the update, the snow
        /// system's own value (set whenever the simulation runs) won on some frames and the snow flickered.
        /// </summary>
        private void OnBeginRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (m_Chart)
            {
                Shader.SetGlobalVector(kSnowScale, kNoSnow);
            }
        }

        protected override void OnUpdate()
        {
            bool chart = IfrChartSystem.IsActive(m_ToolSystem) || AirspaceOverlaySystem.IsActive(m_ToolSystem);
            if (chart == m_Chart)
            {
                if (chart)
                {
                    // Also every update: the snow system sets its scale again whenever the simulation runs.
                    Shader.SetGlobalVector(kSnowScale, kNoSnow);
                    UpdateExposure();
                }
                return;
            }
            m_Chart = chart;
            m_Volume.enabled = chart;
            if (chart)
            {
                m_OverrideTime = m_Planetary.overrideTime;
                m_Time = m_Planetary.time;
                m_Day = m_Planetary.day;
                m_Planetary.overrideTime = true;
                m_Planetary.time = kChartTime;
                m_Planetary.day = kChartDay;
            }
            else
            {
                // As before: an override someone else had keeps its time; without one the clock runs again by itself.
                m_Planetary.overrideTime = m_OverrideTime;
                if (m_OverrideTime)
                {
                    m_Planetary.time = m_Time;
                    m_Planetary.day = m_Day;
                }
            }
            if (chart)
            {
                m_SnowScale = Shader.GetGlobalVector(kSnowScale);
                // Falling rain and snow: the weather effect ClimateRenderSystem makes (its own game object), hidden too.
                if (m_Precipitation == null)
                {
                    m_Precipitation = GameObject.Find("PrecipitationVFX");
                }
                if (m_Precipitation != null)
                {
                    m_PrecipitationActive = m_Precipitation.activeSelf;
                    m_Precipitation.SetActive(false);
                }
                Shader.SetGlobalVector(kSnowScale, kNoSnow);
            }
            else
            {
                // Back as it was (the snow system updates it again once the simulation runs).
                Shader.SetGlobalVector(kSnowScale, m_SnowScale);
                if (m_Precipitation != null)
                {
                    m_Precipitation.SetActive(m_PrecipitationActive);
                }
            }
            // The sun as the game's shadow setting finds it.
            if (m_Sun == null)
            {
                GameObject sun = GameObject.FindGameObjectWithTag("SunLight");
                m_Sun = sun != null ? sun.GetComponent<HDAdditionalLightData>() : null;
            }
            if (m_Sun == null)
            {
                return;
            }
            Light light = m_Sun.GetComponent<Light>();
            if (chart)
            {
                m_SunShadows = light != null && light.shadows != LightShadows.None;
                m_Sun.EnableShadows(false);
                UpdateExposure();
            }
            else
            {
                m_Sun.EnableShadows(m_SunShadows);
            }
        }

        /// <summary>
        /// The fixed exposure from the sun's light (every update while a chart is open: the light reaches the chart's fixed
        /// hour a frame after the clock is set). Automatic exposure until the sun is found.
        /// </summary>
        private void UpdateExposure()
        {
            Light light = m_Sun != null ? m_Sun.GetComponent<Light>() : null;
            float lux = light != null ? light.intensity : 0f;
            m_Exposure.mode.overrideState = lux > 0f;
            if (lux <= 0f)
            {
                return;
            }
            float ev = Mathf.Log(lux / (Mathf.PI * 1.2f), 2f) + kExposureBias;
            if (Mathf.Abs(ev - m_Exposure.fixedExposure.value) > 0.01f)
            {
                m_Exposure.fixedExposure.Override(ev);
                Mod.Log.Info($"Chart exposure: sun {lux:0} lux, EV100 {ev:0.00}");
            }
        }
    }
}
