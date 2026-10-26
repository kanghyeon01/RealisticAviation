using Colossal.Entities;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// The IFR chart's paper background, coloured as the game colours the terrain for an infoview (no flicker, unlike a
    /// semi-transparent overlay, which the game dithers): an overlay texture, one channel per active terrain colour entry
    /// (OverlayInfomodeSystem: channel = the entry's colour index - 1 on the ground, the water entry's index - 5 on the water),
    /// in that entry's colours. Right after the game has set its own texture, this puts in one with the paper entry's
    /// channels full, while the IFR chart is open and the entry ticked.
    /// </summary>
    public partial class IfrChartPaperSystem : GameSystemBase
    {

        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private TerrainRenderSystem m_TerrainRenderSystem;
        private WaterRenderSystem m_WaterRenderSystem;
        private Texture2D m_Ground;
        private Texture2D m_Water;
        private int m_GroundChannel = -1;
        private int m_WaterChannel = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TerrainRenderSystem = World.GetOrCreateSystemManaged<TerrainRenderSystem>();
            m_WaterRenderSystem = World.GetOrCreateSystemManaged<WaterRenderSystem>();
            m_Ground = CreateTexture("RealisticAviationPaperGround");
            m_Water = CreateTexture("RealisticAviationPaperWater");
        }

        protected override void OnDestroy()
        {
            Object.Destroy(m_Ground);
            Object.Destroy(m_Water);
            base.OnDestroy();
        }

        private static Texture2D CreateTexture(string name)
        {
            // The same colour everywhere: a few texels are enough.
            return new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        }

        protected override void OnUpdate()
        {
            InfomodePrefab paper = IfrChartSystem.Paper;
            if (paper == null || !IfrChartSystem.IsActive(m_ToolSystem)
                || !EntityManager.TryGetComponent(m_PrefabSystem.GetEntity(paper), out Game.Prefabs.InfomodeActive active))
            {
                return;
            }
            Fill(m_Ground, active.m_Index - 1, ref m_GroundChannel);
            m_TerrainRenderSystem.overrideOverlaymap = m_Ground;
            // The water: its own entry (its own gain), hidden in the legend and on with the paper.
            InfomodePrefab water = IfrChartSystem.PaperWater;
            if (water != null && EntityManager.TryGetComponent(m_PrefabSystem.GetEntity(water), out Game.Prefabs.InfomodeActive waterActive)
                && waterActive.m_Index >= 5)
            {
                Fill(m_Water, waterActive.m_Index - 5, ref m_WaterChannel);
                m_WaterRenderSystem.overrideOverlaymap = m_Water;
            }
        }

        /// <summary>The texture with only <paramref name="channel"/> full, written again only when the channel changes.</summary>
        private static void Fill(Texture2D texture, int channel, ref int written)
        {
            if (channel == written || channel < 0 || channel > 3)
            {
                return;
            }
            written = channel;
            NativeArray<byte> data = texture.GetRawTextureData<byte>();
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i & 3) == channel ? 255 : 0);
            }
            texture.Apply();
        }
    }
}
