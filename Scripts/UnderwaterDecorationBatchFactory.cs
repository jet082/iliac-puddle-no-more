// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters
{
    internal static class UnderwaterDecorationBatchFactory
    {
        public const string GroupName = "DeepWaters_DecorationBatch";

        // Per-decoration size variation. DaggerfallBillboardBatch's
        // customScale field is in Daggerfall "twips" where divisor = 256,
        // so for a target size multiplier m, scale = (m - 1) * 256.
        //   m = 0.70 -> scale = -76.8
        //   m = 1.00 -> scale = 0  (vanilla size)
        //   m = 1.20 -> scale = +51.2
        // Only the replacement-aware code path supports per-item scale
        // (CustomInfo overload); the vanilla archive batch path stays at
        // the material's authored size.
        internal const float DecorationScaleMin = 0.70f;
        internal const float DecorationScaleMax = 1.20f;
        private const string UnderwaterDecorationShaderName = "DeepWaters/UnderwaterBillboardBatchUnlit";
        private const string UnderwaterDecorationShaderAssetName = "UnderwaterBillboardBatchUnlit.shader";
        private const string TransparentCutoutRenderType = "TransparentCutout";
        private static readonly Color UnderwaterDecorationColor = new Color(1.12f, 1.12f, 1.12f, 1f);
        private static bool loggedMissingUnderwaterDecorationShader;
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int CutoffProperty = Shader.PropertyToID("_Cutoff");

        public static void Spawn(Transform terrainParent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            if (terrainParent == null || positions == null || positions.Count == 0)
                return;

            Transform group = CreateDecorationGroup(terrainParent).transform;
            if (DaggerfallUnity.Settings.AssetInjection)
                SpawnReplacementAwareBillboards(group, positions);
            else
                SpawnArchiveBillboards(group, positions);
        }

        private static GameObject CreateDecorationGroup(Transform parent)
        {
            var group = new GameObject(GroupName);
            group.transform.parent = parent;
            group.transform.localPosition = Vector3.zero;
            group.transform.localRotation = Quaternion.identity;
            group.transform.localScale = Vector3.one;
            return group;
        }

        private static void SpawnArchiveBillboards(Transform parent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            // DFU's native billboard batch handles animated flats itself: it
            // builds the archive atlas and cycles mesh UVs per frame
            // (AnimateUVJob), so animated and static records share one batch
            // per archive — same as DFU's own nature flats.
            var archivePositions = new Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>>();
            for (int i = 0; i < positions.Count; i++)
                AddArchivePosition(archivePositions, positions[i]);

            if (archivePositions.Count > 0)
                SpawnArchiveBillboards(parent, archivePositions);
        }

        private static void SpawnArchiveBillboards(
            Transform parent,
            Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>> archivePositions)
        {
            foreach (KeyValuePair<int, List<DaggerfallBillboardBatch.BasicInfo>> pair in archivePositions)
                SpawnArchiveBillboards(parent, pair.Key, pair.Value);
        }

        private static void SpawnArchiveBillboards(
            Transform parent,
            int archive,
            List<DaggerfallBillboardBatch.BasicInfo> positions)
        {
            DaggerfallBillboardBatch batch = GameObjectHelper.CreateBillboardBatchGameObject(
                archive,
                parent);
            if (batch == null)
                return;

            batch.Clear();
            batch.gameObject.name = "DeepWaters_DecorationArchiveBatch_" + archive;
            ApplyArchiveAnimationSpeed(batch, archive);

            AddArchiveItems(batch, positions);

            batch.Apply();
            ApplyUnderwaterDecorationMaterial(batch.GetComponent<MeshRenderer>());
            DeepWaterRendering.DisableShadows(batch.gameObject);
        }

        private static void SpawnReplacementAwareBillboards(Transform parent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            var archivePositions = new Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>>();
            var replacementPositions = new Dictionary<UnderwaterDecorationRecord, List<Vector3>>();

            for (int i = 0; i < positions.Count; i++)
            {
                UnderwaterDecorationPlacementInfo item = positions[i];
                UnderwaterDecorationRecord record = item.ToRecord();
                if (UnderwaterDecorationCatalog.UsesArchiveAnimation(record))
                {
                    // Asset injection (DREAM etc.): route animated flats through
                    // DFU's native billboard batch rather than the custom atlas
                    // batch. DFU's batch loads the replacement atlas and animates
                    // by cycling mesh UVs, which survives our unlit-material swap,
                    // so DREAM's animated replacements render correctly. The
                    // custom atlas path mishandled replacement textures. (issue 11)
                    AddArchivePosition(archivePositions, item);
                    continue;
                }

                UnderwaterDecorationReplacementInfo replacementInfo;
                if (!UnderwaterDecorationReplacementCache.TryGet(record, out replacementInfo))
                {
                    AddArchivePosition(archivePositions, item);
                    continue;
                }

                List<Vector3> recordPositions;
                if (!replacementPositions.TryGetValue(record, out recordPositions))
                {
                    recordPositions = new List<Vector3>();
                    replacementPositions.Add(record, recordPositions);
                }

                recordPositions.Add(item.LocalPosition);
            }

            if (archivePositions.Count > 0)
                SpawnArchiveBillboards(parent, archivePositions);

            foreach (KeyValuePair<UnderwaterDecorationRecord, List<Vector3>> pair in replacementPositions)
            {
                UnderwaterDecorationReplacementInfo replacementInfo;
                if (UnderwaterDecorationReplacementCache.TryGet(pair.Key, out replacementInfo))
                    SpawnReplacementBatch(parent, pair.Key, pair.Value, replacementInfo);
            }
        }

        private static void SpawnReplacementBatch(
            Transform parent,
            UnderwaterDecorationRecord record,
            List<Vector3> positions,
            UnderwaterDecorationReplacementInfo info)
        {
            DaggerfallBillboardBatch batch = GameObjectHelper.CreateBillboardBatchGameObject(
                record.Archive,
                parent);
            if (batch == null)
                return;

            batch.Clear();
            batch.gameObject.name =
                "DeepWaters_DecorationReplacementBatch_" + record.Archive + "_" + record.Record;
            batch.SetMaterial(info.Material);

            AddReplacementItems(batch, positions, info);

            batch.Apply();
            ApplyUnderwaterDecorationMaterial(batch.GetComponent<MeshRenderer>());
            DeepWaterRendering.DisableShadows(batch.gameObject);
        }

        private static void AddArchivePosition(
            Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>> archivePositions,
            UnderwaterDecorationPlacementInfo item)
        {
            List<DaggerfallBillboardBatch.BasicInfo> positions;
            if (!archivePositions.TryGetValue(item.Archive, out positions))
            {
                positions = new List<DaggerfallBillboardBatch.BasicInfo>();
                archivePositions.Add(item.Archive, positions);
            }

            positions.Add(new DaggerfallBillboardBatch.BasicInfo(item.Record, item.LocalPosition));
        }

        private static void ApplyArchiveAnimationSpeed(DaggerfallBillboardBatch batch, int archive)
        {
            float framesPerSecond;
            if (UnderwaterDecorationCatalog.TryGetFramesPerSecond(archive, out framesPerSecond))
                batch.FramesPerSecond = framesPerSecond;
        }

        // Items are added through the per-item path, NOT AddItemsAsync.
        // AddItemsAsync reads billboardData.Length from the main thread right
        // after scheduling a job that writes that list (DaggerfallBillboardBatch
        // line ~517); with the editor's collection safety checks enabled that
        // read throws InvalidOperationException on every call, aborting the
        // batch mid-population (missing decorations) and leaking the job's
        // TempJob arrays. AddItem is obsolete only as a performance hint —
        // it completes pending jobs and adds on the main thread, which is
        // correct everywhere and trivial at per-tile decoration counts.
        private static void AddArchiveItems(
            DaggerfallBillboardBatch batch,
            List<DaggerfallBillboardBatch.BasicInfo> positions)
        {
            if (batch == null || positions == null || positions.Count == 0)
                return;

#pragma warning disable 618
            for (int i = 0; i < positions.Count; i++)
                batch.AddItem(positions[i].textureRecord, positions[i].localPosition);
#pragma warning restore 618
        }

        private static void AddReplacementItems(
            DaggerfallBillboardBatch batch,
            List<Vector3> positions,
            UnderwaterDecorationReplacementInfo info)
        {
            if (batch == null || positions == null || positions.Count == 0)
                return;

            // Per-item path for the same reason as AddArchiveItems (see note
            // there). This is also the only API that works with a custom
            // (replacement) material.
#pragma warning disable 618
            for (int i = 0; i < positions.Count; i++)
            {
                float sizeFactor = UnityEngine.Random.Range(DecorationScaleMin, DecorationScaleMax);
                float scaleValue = (sizeFactor - 1f) * BlocksFile.ScaleDivisor;
                batch.AddItem(
                    info.Rect,
                    info.BatchSize,
                    new Vector2(scaleValue, scaleValue),
                    positions[i]);
            }
#pragma warning restore 618
        }

        // Internal so loot/wreck billboards can reuse the same unlit, brightened
        // material. DFU single billboards and billboard batches share the same
        // tangent-encoded mesh + shader-driven facing, so this material renders
        // correctly on both. (issue 12: loot/wreck flats render dark)
        internal static void ApplyUnderwaterDecorationMaterial(Renderer renderer)
        {
            if (renderer == null)
                return;

            Material underwaterMaterial = CreateUnderwaterDecorationMaterial(renderer.sharedMaterial);
            if (underwaterMaterial == null)
            {
                ConfigureUnderwaterDecorationMaterial(renderer.sharedMaterial);
                return;
            }

            renderer.sharedMaterial = underwaterMaterial;

            var owner = renderer.GetComponent<OwnedUnderwaterDecorationMaterial>();
            if (owner == null)
                owner = renderer.gameObject.AddComponent<OwnedUnderwaterDecorationMaterial>();

            owner.Set(underwaterMaterial);
        }

        private static Material CreateUnderwaterDecorationMaterial(Material sourceMaterial)
        {
            if (sourceMaterial == null)
                return null;

            Shader shader = LoadUnderwaterDecorationShader();
            if (shader == null)
                return null;

            var material = new Material(shader)
            {
                name = sourceMaterial.name + " (Deep Waters Underwater)",
            };

            CopyTextureAndTransform(sourceMaterial, material, Uniforms.MainTex);
            ConfigureUnderwaterDecorationMaterial(material);
            return material;
        }

        private static Shader LoadUnderwaterDecorationShader()
        {
            Shader shader = Shader.Find(UnderwaterDecorationShaderName);

            if (shader == null && DeepWaters.Mod != null)
                shader = DeepWaters.Mod.GetAsset<Shader>(UnderwaterDecorationShaderAssetName);

            if (shader == null && !loggedMissingUnderwaterDecorationShader)
            {
                Debug.LogWarning(
                    "[DeepWaters] " + UnderwaterDecorationShaderName +
                    " shader not found. Underwater decorations will use the vanilla billboard material.");
                loggedMissingUnderwaterDecorationShader = true;
            }

            return shader;
        }

        private static void CopyTextureAndTransform(Material sourceMaterial, Material targetMaterial, int propertyId)
        {
            if (sourceMaterial == null ||
                targetMaterial == null ||
                !sourceMaterial.HasProperty(propertyId) ||
                !targetMaterial.HasProperty(propertyId))
            {
                return;
            }

            Texture texture = sourceMaterial.GetTexture(propertyId);
            if (texture != null)
                targetMaterial.SetTexture(propertyId, texture);

            targetMaterial.SetTextureScale(propertyId, sourceMaterial.GetTextureScale(propertyId));
            targetMaterial.SetTextureOffset(propertyId, sourceMaterial.GetTextureOffset(propertyId));
        }

        private static void ConfigureUnderwaterDecorationMaterial(Material material)
        {
            if (material == null)
                return;

            material.SetOverrideTag("RenderType", TransparentCutoutRenderType);
            material.renderQueue = (int)RenderQueue.AlphaTest;

            if (material.HasProperty(ColorProperty))
                material.SetColor(ColorProperty, UnderwaterDecorationColor);

            if (material.HasProperty(CutoffProperty))
                material.SetFloat(CutoffProperty, 0.5f);
        }

        private sealed class OwnedUnderwaterDecorationMaterial : MonoBehaviour
        {
            private Material material;

            public void Set(Material value)
            {
                if (material != null && material != value)
                    Destroy(material);

                material = value;
            }

            private void OnDestroy()
            {
                if (material != null)
                    Destroy(material);
            }
        }

    }
}
