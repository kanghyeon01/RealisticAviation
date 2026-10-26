using System.Threading.Tasks;
using Colossal.Entities;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// A terrain colour entry of the VFR chart's legend (elevation tints, built-up areas, water), as the game's heatmaps: colour
    /// group 0 (the ground), or 1 for the water. Not a heatmap prefab, so the game's OverlayInfomodeSystem writes nothing for it;
    /// <see cref="VfrChartTintSystem"/> fills its channel. The legend shows the true colours; the shader gets them
    /// <see cref="m_Brightness"/> times as bright (as the IFR chart's background: at 1 the lit terrain shows them dark).
    /// </summary>
    internal sealed class TerrainTintInfomodePrefab : GradientInfomodeBasePrefab
    {
        public float m_Brightness = 1f;

        /// <summary>Per-channel gain in linear light (screen colour against the paper chart's, from screenshots).</summary>
        public Vector3 m_Gain = Vector3.one;

        /// <summary>
        /// An elevation tint group (TerrainChartMap): the shader gets transparent where the channel is empty, the group'"'"'s
        /// first tint at half and its last at full, in fine steps, whatever the legend shows (m_Low / m_Medium / m_High).
        /// </summary>
        public bool m_TintGroup;
        public Color m_GroupFirst;
        public Color m_GroupLast;
        private const float kGroupSteps = 255f;

        /// <summary>A water entry (colour group 1: water surfaces) instead of a ground one.</summary>
        public bool m_Water;

        /// <summary>The share of the colour shown where the channel is empty (0: the map as it is).</summary>
        public float m_LowAlpha = 1f;

        public override string infomodeTypeLocaleKey => "TerrainColor";

        public override int GetColorGroup(out int secondaryGroup)
        {
            secondaryGroup = -1;
            if (m_Water)
            {
                return 1;
            }
            return 0;
        }

        public override void GetColors(out Color color0, out Color color1, out Color color2, out float steps, out float speed, out float tiling, out float fill)
        {
            base.GetColors(out color0, out color1, out color2, out steps, out speed, out tiling, out fill);
            if (m_TintGroup)
            {
                color0 = Bright(m_GroupFirst, 0f, m_FirstGain);
                color1 = Bright(m_GroupFirst, 1f, m_FirstGain);
                color2 = Bright(m_GroupLast, 1f, m_LastGain);
                steps = kGroupSteps;
                return;
            }
            color0 = Bright(color0, m_LowAlpha, m_Gain);
            color1 = Bright(color1, color1.a, m_Gain);
            color2 = Bright(color2, color2.a, m_Gain);
        }

        /// <summary>
        /// A tint group's gains for its first and last tint, used instead of <see cref="m_Gain"/>: one gain does not fit every
        /// tint (the game keeps some of the ground's own colour under a tint, so the warm tints came out lemon at the low
        /// ground's gain).
        /// </summary>
        public Vector3 m_FirstGain = Vector3.one;
        public Vector3 m_LastGain = Vector3.one;

        /// <summary>
        /// The colour the shader gets: brightness and the per-channel gain applied in linear light (the game hands the shader
        /// the colour's linear value), so what shows on screen matches the paper chart's colour (calibrated from screenshots
        /// under the charts' fixed daylight).
        /// </summary>
        private Color Bright(Color color, float alpha, Vector3 gain)
        {
            Color linear = color.linear;
            linear = new Color(linear.r * m_Brightness * gain.x, linear.g * m_Brightness * gain.y, linear.b * m_Brightness * gain.z, 1f);
            Color gamma = linear.gamma;
            return new Color(gamma.r, gamma.g, gamma.b, alpha);
        }
    }

    /// <summary>
    /// Colours the VFR chart's elevation tints and built-up areas into the terrain, as the game colours it for its own
    /// heatmaps (smooth, no flicker; the semi-transparent overlay rectangles drawn before flickered and showed their
    /// steps): an overlay texture over the playable area, one channel per active entry (channel = its colour index - 1).
    /// Right after the game has set its own texture, this puts in one with the bands and the built-up cells, written again
    /// only when a new sampling or count is in or an entry's channel changes.
    /// </summary>
    public partial class VfrChartTintSystem : GameSystemBase
    {
        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private TerrainRenderSystem m_TerrainRenderSystem;
        private WaterRenderSystem m_WaterRenderSystem;
        private Texture2D m_WaterTexture;
        private int m_WaterChannel = -1;
        private AirspaceOverlaySystem m_Chart;
        private Texture2D m_Texture;
        private int m_TerrainChannels = -2;
        private int m_UrbanChannel = -2;
        private int m_TerrainVersion = -1;
        private int m_UrbanVersion = -1;

        /// <summary>Texels a tint channel reaches into its empty neighbours (<see cref="Spread"/>).</summary>
        private const int kSpread = 2;
        private byte[] m_Spread;
        private byte[] m_Pixels;
        private Task m_Writing;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TerrainRenderSystem = World.GetOrCreateSystemManaged<TerrainRenderSystem>();
            m_WaterRenderSystem = World.GetOrCreateSystemManaged<WaterRenderSystem>();
            m_WaterTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "RealisticAviationVfrWater", wrapMode = TextureWrapMode.Clamp };
            m_Chart = World.GetOrCreateSystemManaged<AirspaceOverlaySystem>();
            m_Texture = new Texture2D(TerrainChartMap.kTexels, TerrainChartMap.kTexels, TextureFormat.RGBA32, false, true)
            {
                name = "RealisticAviationVfrTints",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        protected override void OnDestroy()
        {
            m_Writing?.Wait();
            Object.Destroy(m_Texture);
            Object.Destroy(m_WaterTexture);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (!AirspaceOverlaySystem.IsActive(m_ToolSystem))
            {
                m_TerrainVersion = m_UrbanVersion = -1;
                return;
            }
            // The tint groups follow the legend's "terrain elevations" (the first group's entry; the others are hidden).
            int terrain = Channel(AirspaceOverlaySystem.TerrainInfomode(0));
            int second = terrain >= 0 ? Channel(AirspaceOverlaySystem.TerrainInfomode(1)) : -1;
            int third = terrain >= 0 ? Channel(AirspaceOverlaySystem.TerrainInfomode(2)) : -1;
            int channels = (terrain + 1) | (second + 1) << 3 | (third + 1) << 6;
            int urban = Channel(AirspaceOverlaySystem.UrbanInfomode);
            TerrainChartMap terrainMap = m_Chart.TerrainMap;
            UrbanAreaMap urbanMap = m_Chart.UrbanMap;
            // The texture is written on a worker thread (millions of texels: on the main thread the game stalled each time
            // the chart opened, user) and copied in once done.
            if (m_Writing != null && m_Writing.IsCompleted)
            {
                if (m_Writing.IsFaulted)
                {
                    Mod.Log.Error(m_Writing.Exception, "The VFR chart texture was not written");
                }
                else
                {
                    m_Texture.SetPixelData(m_Pixels, 0);
                    m_Texture.Apply();
                }
                m_Writing = null;
            }
            if (m_Writing == null && (channels != m_TerrainChannels || urban != m_UrbanChannel || terrainMap.Version != m_TerrainVersion || urbanMap.Version != m_UrbanVersion))
            {
                m_TerrainChannels = channels;
                m_UrbanChannel = urban;
                m_TerrainVersion = terrainMap.Version;
                m_UrbanVersion = urbanMap.Version;
                m_Writing = Task.Run(() => Write(terrainMap, urbanMap, terrain, second, third, urban));
            }
            m_TerrainRenderSystem.overrideOverlaymap = m_Texture;
            // The water surfaces in the chart's blue: the water entry's channel (colour index - 5) full everywhere.
            int water = WaterChannel();
            if (water >= 0)
            {
                if (water != m_WaterChannel)
                {
                    m_WaterChannel = water;
                    NativeArray<byte> waterData = m_WaterTexture.GetRawTextureData<byte>();
                    for (int i = 0; i < waterData.Length; i++)
                    {
                        waterData[i] = (byte)((i & 3) == water ? 255 : 0);
                    }
                    m_WaterTexture.Apply();
                }
                m_WaterRenderSystem.overrideOverlaymap = m_WaterTexture;
            }
        }

        /// <summary>
        /// On a worker thread: the tints and the built-up areas into the texture's pixels, in a managed array (much faster to
        /// index than the texture's native data).
        /// </summary>
        private void Write(TerrainChartMap terrainMap, UrbanAreaMap urbanMap, int terrain, int second, int third, int urban)
        {
            if (m_Pixels == null)
            {
                m_Pixels = new byte[TerrainChartMap.kTexels * TerrainChartMap.kTexels * 4];
            }
            System.Array.Clear(m_Pixels, 0, m_Pixels.Length);
            if (terrain >= 0 || second >= 0 || third >= 0)
            {
                terrainMap.WriteTints(m_Pixels, terrain, second, third);
                Spread(m_Pixels, terrain);
                Spread(m_Pixels, second);
                Spread(m_Pixels, third);
            }
            if (urban >= 0)
            {
                urbanMap.WriteUrban(m_Pixels, urban);
            }
        }

        /// <summary>
        /// A tint channel spread <see cref="kSpread"/> texels into the empty texels round it (the next group's, and the water's).
        /// The terrain shader filters the texture between texels whatever its filter mode, so where one group met the next,
        /// both channels faded out together and the bare ground showed through: a white stripe at every group edge and along
        /// the shore (user). Overlapping, one group's fade lies under the other's full colour.
        /// </summary>
        private void Spread(byte[] texture, int channel)
        {
            if (channel < 0)
            {
                return;
            }
            const int texels = TerrainChartMap.kTexels;
            if (m_Spread == null)
            {
                m_Spread = new byte[texels * texels];
            }
            // Along the rows into a scratch grid, then along the columns back into the texture: a square spread.
            for (int z = 0; z < texels; z++)
            {
                for (int x = 0; x < texels; x++)
                {
                    int index = (x + z * texels) * 4 + channel;
                    byte value = texture[index];
                    if (value == 0)
                    {
                        for (int d = 1; d <= kSpread; d++)
                        {
                            value = (byte)math.max(value, math.max(x - d >= 0 ? texture[index - d * 4] : 0, x + d < texels ? texture[index + d * 4] : 0));
                        }
                    }
                    m_Spread[x + z * texels] = value;
                }
            }
            for (int z = 0; z < texels; z++)
            {
                for (int x = 0; x < texels; x++)
                {
                    int cell = x + z * texels;
                    byte value = m_Spread[cell];
                    if (value == 0)
                    {
                        for (int d = 1; d <= kSpread; d++)
                        {
                            value = (byte)math.max(value, math.max(z - d >= 0 ? m_Spread[cell - d * texels] : 0, z + d < texels ? m_Spread[cell + d * texels] : 0));
                        }
                    }
                    texture[cell * 4 + channel] = value;
                }
            }
        }

        /// <summary>The water texture channel of the ticked water entry, or -1.</summary>
        private int WaterChannel()
        {
            InfomodePrefab infomode = AirspaceOverlaySystem.WaterInfomode;
            if (infomode == null || !EntityManager.TryGetComponent(m_PrefabSystem.GetEntity(infomode), out Game.Prefabs.InfomodeActive active))
            {
                return -1;
            }
            int channel = active.m_Index - 5;
            return channel >= 0 && channel < 4 ? channel : -1;
        }

        /// <summary>The texture channel of a ticked ground entry, or -1.</summary>
        private int Channel(InfomodePrefab infomode)
        {
            if (infomode == null || !EntityManager.TryGetComponent(m_PrefabSystem.GetEntity(infomode), out Game.Prefabs.InfomodeActive active))
            {
                return -1;
            }
            int channel = active.m_Index - 1;
            return channel >= 0 && channel < 4 ? channel : -1;
        }
    }
}
