// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using Unity.Jobs;

namespace DeepWaters
{
    /// <summary>
    /// Decorates DFU terrain texturing without changing its tile choices.
    ///
    /// Earlier builds converted pure-water terrain tiles (value 0) to dirt
    /// before holes were applied. That made the retained shore buffer draw as
    /// bright land/snow through the transparent water surface, producing a crisp
    /// contour line from above. Holed cells are removed anyway, and the retained
    /// buffer should visually read as water, so this wrapper now preserves DFU's
    /// original water/shore texture classifications.
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
