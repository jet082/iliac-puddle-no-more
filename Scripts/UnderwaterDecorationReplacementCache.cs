// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility.AssetInjection;
using UnityEngine;

namespace DeepWaters
{
    internal sealed class UnderwaterDecorationReplacementInfo
    {
        public bool HasMaterial;
        public bool HasReplacement;
        public bool HasImportedAnimation;
        public Material Material;
        public Rect Rect;
        public Vector2 BatchSize;
    }

    internal static class UnderwaterDecorationReplacementCache
    {
        private static readonly Dictionary<UnderwaterDecorationRecord, UnderwaterDecorationReplacementInfo> cache =
            new Dictionary<UnderwaterDecorationRecord, UnderwaterDecorationReplacementInfo>();

        public static bool TryGet(UnderwaterDecorationRecord record, out UnderwaterDecorationReplacementInfo info)
        {
            if (cache.TryGetValue(record, out info))
                return info.HasReplacement;

            info = Probe(record);
            cache.Add(record, info);
            return info.HasReplacement;
        }

        public static bool TryGetMaterial(UnderwaterDecorationRecord record, out UnderwaterDecorationReplacementInfo info)
        {
            if (!cache.TryGetValue(record, out info))
            {
                info = Probe(record);
                cache.Add(record, info);
            }

            return info.HasMaterial;
        }

        private static UnderwaterDecorationReplacementInfo Probe(UnderwaterDecorationRecord record)
        {
            var info = new UnderwaterDecorationReplacementInfo();
            GameObject probe = new GameObject("DeepWaters_DecorationReplacementProbe");
            probe.hideFlags = HideFlags.HideAndDontSave;

            BillboardSummary summary = new BillboardSummary();
            Vector2 replacementScale;
            Material material = TextureReplacement.GetStaticBillboardMaterial(
                probe,
                record.Archive,
                record.Record,
                ref summary,
                out replacementScale);

            if (material != null && material.mainTexture != null)
                FillReplacementInfo(record, material, summary.Rect, replacementScale, summary, info);
            else
                FillVanillaMaterialInfo(record, info);

            Object.Destroy(probe);
            return info;
        }

        private static void FillReplacementInfo(
            UnderwaterDecorationRecord record,
            Material material,
            Rect rect,
            Vector2 replacementScale,
            BillboardSummary summary,
            UnderwaterDecorationReplacementInfo info)
        {
            FillMaterialInfo(record, material, rect, replacementScale, info);
            info.HasReplacement = info.HasMaterial;
            info.HasImportedAnimation = info.HasMaterial &&
                                        summary.ImportedTextures.HasImportedTextures &&
                                        summary.ImportedTextures.FrameCount > 1;
        }

        private static void FillVanillaMaterialInfo(
            UnderwaterDecorationRecord record,
            UnderwaterDecorationReplacementInfo info)
        {
            if (DaggerfallUnity.Instance == null || DaggerfallUnity.Instance.MaterialReader == null)
                return;

            Rect rect;
            Material material = DaggerfallUnity.Instance.MaterialReader.GetMaterial(
                record.Archive,
                record.Record,
                0,
                0,
                out rect,
                4,
                true,
                true);

            FillMaterialInfo(record, material, rect, Vector2.one, info);
        }

        private static void FillMaterialInfo(
            UnderwaterDecorationRecord record,
            Material material,
            Rect rect,
            Vector2 scale,
            UnderwaterDecorationReplacementInfo info)
        {
            Vector2 baseSize;
            Mesh mesh = DaggerfallUnity.Instance.MeshReader.GetBillboardMesh(
                rect,
                record.Archive,
                record.Record,
                out baseSize);

            if (mesh != null)
                Object.Destroy(mesh);

            info.HasMaterial = material != null &&
                               material.mainTexture != null &&
                               baseSize.x > 0f &&
                               baseSize.y > 0f;
            info.Material = material;
            info.Rect = rect;
            info.BatchSize = new Vector2(
                baseSize.x * scale.x / MeshReader.GlobalScale,
                baseSize.y * scale.y / MeshReader.GlobalScale);
        }
    }
}
