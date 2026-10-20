using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>One runway end's approach surface (see <see cref="ApproachZoneSystem"/>).</summary>
    internal struct ApproachCorridor
    {
        /// <summary>The runway end airplanes land over.</summary>
        public float2 m_Threshold;

        /// <summary>Away from the runway, along the extended centerline.</summary>
        public float2 m_Outward;

        public float m_Height;
        public float m_GlideTan;

        /// <summary>Out to the final approach fix.</summary>
        public float m_Length;

        public bool Equals(in ApproachCorridor other)
        {
            return m_Threshold.Equals(other.m_Threshold) && m_Outward.Equals(other.m_Outward) && m_Height == other.m_Height
                && m_GlideTan == other.m_GlideTan && m_Length == other.m_Length;
        }
    }

    /// <summary>
    /// With the glide path option, arrivals fly a long, low final, mostly outside the protection of the game's own airspace
    /// nets (1 km long, rising at 11 degrees). This keeps zoned buildings under each approach out of their way: under an
    /// approach surface, from the runway end to the final approach fix, kClearance below the glide path, the zone cells'
    /// height limit (the one the airspace nets set, used when buildings spawn and level up) is lowered to the surface.
    /// Only ever lowered: where the game's own limit is lower it stays. Existing buildings are not touched, as with the
    /// game's limit.
    ///
    /// The game recomputes cell heights whenever a block's surroundings change, so the surface is applied again every
    /// kInterval frames. When the surfaces change (option off, runways built or removed), the blocks under the old ones are
    /// marked updated, and the game puts its own limits back.
    /// </summary>
    public partial class ApproachZoneSystem : GameSystemBase
    {
        /// <summary>The surface lies this far below the glide path.</summary>
        private const float kClearance = 50f;

        /// <summary>
        /// Width as the approach surface of an instrument runway (14 CFR 77.19(d)): 1,000 ft wide at the runway end, and
        /// widening by 15% of the distance to each side.
        /// </summary>
        private const float kInnerHalfWidth = 152f;
        private const float kFlare = 0.15f;

        private const int kInterval = 32;

        /// <summary>The longest surface: to the final approach fix (1,500 ft) at the shallowest glide path, 3 degrees.</summary>
        private const float kLongestSurface = 8800f;

        private AtcSystem m_AtcSystem;
        private SearchSystem m_ZoneSearchSystem;
        private BufferLookup<Cell> m_CellData;
        private BufferLookup<VacantLot> m_VacantLotData;
        private ComponentLookup<Block> m_BlockData;
        private readonly List<ApproachCorridor> m_Corridors = new List<ApproachCorridor>();
        private readonly List<ApproachCorridor> m_Applied = new List<ApproachCorridor>();
        private bool m_ResetAfterLoad;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kInterval;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_ZoneSearchSystem = World.GetOrCreateSystemManaged<SearchSystem>();
            m_CellData = GetBufferLookup<Cell>(false);
            m_VacantLotData = GetBufferLookup<VacantLot>(false);
            m_BlockData = GetComponentLookup<Block>(true);
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_Applied.Clear();
            m_ResetAfterLoad = true;
        }

        protected override void OnUpdate()
        {
            m_Corridors.Clear();
            m_AtcSystem.GetApproachCorridors(m_Corridors);
            if (m_ResetAfterLoad && ResetAfterLoad())
            {
                return;
            }
            bool changed = !SameCorridors();
            if (m_Corridors.Count == 0 && !changed)
            {
                return;
            }
            CompleteDependency();
            NativeQuadTree<Entity, Bounds2> tree = m_ZoneSearchSystem.GetSearchTree(true, out JobHandle treeDependency);
            treeDependency.Complete();
            m_CellData.Update(this);
            m_VacantLotData.Update(this);
            m_BlockData.Update(this);

            NativeList<Entity> blocks = new NativeList<Entity>(64, Allocator.Temp);
            if (changed)
            {
                // The game's own limits back under the surfaces that are gone or moved.
                for (int i = 0; i < m_Applied.Count; i++)
                {
                    CollectBlocks(tree, m_Applied[i], blocks);
                }
                int marked = MarkUpdated(blocks);
                if (Mod.Setting != null && Mod.Setting.DetailedLog)
                {
                    Mod.Log.Info($"approach surfaces: {m_Corridors.Count} (was {m_Applied.Count}), {marked} zone blocks given back to the game");
                }
                m_Applied.Clear();
                // The new surfaces are applied from the next sweep, after the game has recomputed the blocks marked here.
                m_Applied.AddRange(m_Corridors);
            }
            else
            {
                int lowered = 0;
                for (int i = 0; i < m_Corridors.Count; i++)
                {
                    blocks.Clear();
                    CollectBlocks(tree, m_Corridors[i], blocks);
                    for (int j = 0; j < blocks.Length; j++)
                    {
                        lowered += Apply(blocks[j], m_Corridors[i]);
                    }
                }
                // Only after the game has (re)computed cells under a surface, so not every sweep.
                if (lowered != 0 && Mod.Setting != null && Mod.Setting.DetailedLog)
                {
                    Mod.Log.Info($"approach surfaces: height limit lowered on {lowered} zone cells under {m_Corridors.Count} surfaces");
                }
            }
            blocks.Dispose();
        }

        /// <summary>
        /// Cell heights are saved with the city, so limits lowered before the save (perhaps with other options) are still
        /// there. Once the runways are known after a load, the blocks under the longest possible surface (3 degrees) of
        /// every runway end are marked updated, and the game puts its own limits back; the current surfaces follow.
        /// Returns true when it did so (or the runways aren't known yet).
        /// </summary>
        private bool ResetAfterLoad()
        {
            List<RunwayEndInfo> ends = new List<RunwayEndInfo>();
            m_AtcSystem.GetRunwayEnds(Entity.Null, ends);
            if (ends.Count == 0)
            {
                // No runways yet (or a city without airports): nothing lowered can be under one.
                return m_Corridors.Count == 0;
            }
            m_ResetAfterLoad = false;
            CompleteDependency();
            NativeQuadTree<Entity, Bounds2> tree = m_ZoneSearchSystem.GetSearchTree(true, out JobHandle treeDependency);
            treeDependency.Complete();
            float glideTan = math.tan(math.radians(3f));
            NativeList<Entity> blocks = new NativeList<Entity>(256, Allocator.Temp);
            for (int i = 0; i < ends.Count; i++)
            {
                ApproachCorridor widest = new ApproachCorridor
                {
                    m_Threshold = ends[i].Start.xz,
                    m_Outward = -ends[i].Direction,
                    m_GlideTan = glideTan,
                    m_Length = kLongestSurface,
                };
                CollectBlocks(tree, widest, blocks);
            }
            int marked = MarkUpdated(blocks);
            blocks.Dispose();
            if (Mod.Setting != null && Mod.Setting.DetailedLog)
            {
                Mod.Log.Info($"approach surfaces: {marked} zone blocks around {ends.Count} runway ends given back to the game after loading");
            }
            return true;
        }

        private int MarkUpdated(NativeList<Entity> blocks)
        {
            int marked = 0;
            for (int i = 0; i < blocks.Length; i++)
            {
                if (EntityManager.Exists(blocks[i]) && !EntityManager.HasComponent<Updated>(blocks[i]))
                {
                    EntityManager.AddComponent<Updated>(blocks[i]);
                    marked++;
                }
            }
            return marked;
        }

        private bool SameCorridors()
        {
            if (m_Corridors.Count != m_Applied.Count)
            {
                return false;
            }
            for (int i = 0; i < m_Corridors.Count; i++)
            {
                if (!m_Corridors[i].Equals(m_Applied[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static void CollectBlocks(NativeQuadTree<Entity, Bounds2> tree, in ApproachCorridor corridor, NativeList<Entity> blocks)
        {
            float2 side = new float2(-corridor.m_Outward.y, corridor.m_Outward.x);
            float2 far = corridor.m_Threshold + corridor.m_Outward * corridor.m_Length;
            float farHalfWidth = kInnerHalfWidth + corridor.m_Length * kFlare;
            Bounds2 bounds = new Bounds2(corridor.m_Threshold, corridor.m_Threshold);
            bounds |= corridor.m_Threshold + side * kInnerHalfWidth;
            bounds |= corridor.m_Threshold - side * kInnerHalfWidth;
            bounds |= far + side * farHalfWidth;
            bounds |= far - side * farHalfWidth;
            BlockIterator iterator = new BlockIterator { m_Bounds = bounds, m_Blocks = blocks };
            tree.Iterate(ref iterator);
        }

        /// <summary>Lowers the height limit of the block's cells under the surface, and of its vacant lots over them.</summary>
        private int Apply(Entity entity, in ApproachCorridor corridor)
        {
            if (!m_CellData.TryGetBuffer(entity, out DynamicBuffer<Cell> cells) || !m_BlockData.TryGetComponent(entity, out Block block))
            {
                return 0;
            }
            float2 side = new float2(-corridor.m_Outward.y, corridor.m_Outward.x);
            int lowered = 0;
            for (int y = 0; y < block.m_Size.y; y++)
            {
                for (int x = 0; x < block.m_Size.x; x++)
                {
                    float2 position = ZoneUtils.GetCellPosition(block, new int2(x, y)).xz - corridor.m_Threshold;
                    float along = math.dot(position, corridor.m_Outward);
                    if (along <= 0f || along > corridor.m_Length || math.abs(math.dot(position, side)) > kInnerHalfWidth + along * kFlare)
                    {
                        continue;
                    }
                    int limit = (int)math.floor(corridor.m_Height + along * corridor.m_GlideTan - kClearance);
                    int index = y * block.m_Size.x + x;
                    Cell cell = cells[index];
                    if (cell.m_Height > limit)
                    {
                        cell.m_Height = (short)math.max(limit, short.MinValue);
                        cells[index] = cell;
                        lowered++;
                    }
                }
            }
            if (lowered == 0 || !m_VacantLotData.TryGetBuffer(entity, out DynamicBuffer<VacantLot> lots))
            {
                return lowered;
            }
            for (int i = 0; i < lots.Length; i++)
            {
                VacantLot lot = lots[i];
                int height = lot.m_Height;
                for (int y = math.max(lot.m_Area.z, 0); y < math.min(lot.m_Area.w, block.m_Size.y); y++)
                {
                    for (int x = math.max(lot.m_Area.x, 0); x < math.min(lot.m_Area.y, block.m_Size.x); x++)
                    {
                        height = math.min(height, cells[y * block.m_Size.x + x].m_Height);
                    }
                }
                if (height < lot.m_Height)
                {
                    lot.m_Height = (short)height;
                    lots[i] = lot;
                }
            }
            return lowered;
        }

        private struct BlockIterator : INativeQuadTreeIterator<Entity, Bounds2>
        {
            public Bounds2 m_Bounds;
            public NativeList<Entity> m_Blocks;

            public bool Intersect(Bounds2 bounds)
            {
                return MathUtils.Intersect(bounds, m_Bounds);
            }

            public void Iterate(Bounds2 bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds, m_Bounds))
                {
                    m_Blocks.Add(item);
                }
            }
        }
    }
}
