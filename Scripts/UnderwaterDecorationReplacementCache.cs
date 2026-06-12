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
                FillReplacementInfo(record, material, summary, replacementScale, info);

            Object.Destroy(probe);
            return info;
        }

        private static void FillReplacementInfo(
            UnderwaterDecorationRecord record,
            Material material,
            BillboardSummary summary,
            Vector2 replacementScale,
            UnderwaterDecorationReplacementInfo info)
        {
            Vector2 baseSize;
            Mesh mesh = DaggerfallUnity.Instance.MeshReader.GetBillboardMesh(
                summary.Rect,
                record.Archive,
                record.Record,
                out baseSize);

            if (mesh != null)
                Object.Destroy(mesh);

            info.HasReplacement = baseSize.x > 0f && baseSize.y > 0f;
            info.HasImportedAnimation = summary.ImportedTextures.HasImportedTextures &&
                                        summary.ImportedTextures.FrameCount > 1;
            info.Material = material;
            info.Rect = summary.Rect;
            info.BatchSize = new Vector2(
                baseSize.x * replacementScale.x / MeshReader.GlobalScale,
                baseSize.y * replacementScale.y / MeshReader.GlobalScale);
        }
    }
}
