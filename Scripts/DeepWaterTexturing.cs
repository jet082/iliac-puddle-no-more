// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using Unity.Jobs;

namespace DeepWaters
{
    /// <summary>
    /// Decorates DFU terrain texturing without changing its tile choices.
    ///
    /// Do not recolor ocean terrain here to simulate transparency. DFU's
    /// terrain material is opaque, so any texture assigned to visible ocean
    /// terrain becomes a hard cap above fish, decorations, and the generated
    /// seafloor. Transparency must come from the generated water surface plus
    /// render-side cap hiding where safe, not from swapping water tiles for
    /// land tiles.
    /// </summary>
    public sealed class DeepWaterTexturing : ITerrainTexturing
    {
        private readonly ITerrainTexturing inner;

        public DeepWaterTexturing(ITerrainTexturing inner)
        {
            this.inner = inner;
        }

        public bool ConvertWaterTiles() => inner.ConvertWaterTiles();

        public JobHandle ScheduleAssignTilesJob(
            ITerrainSampler terrainSampler,
            ref MapPixelData mapData,
            JobHandle dependencies,
            bool march = true)
        {
            return inner.ScheduleAssignTilesJob(terrainSampler, ref mapData, dependencies, march);
        }
    }
}
