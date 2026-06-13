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
        private static readonly Dictionary<Material, Material> underwaterMaterialCache = new Dictionary<Material, Material>();
        private static readonly HashSet<Material> cachedUnderwaterMaterials = new HashSet<Material>();

        public static GameObject Spawn(Transform terrainParent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            if (terrainParent == null || positions == null || positions.Count == 0)
                return null;

            GameObject groupObject = CreateDecorationGroup(terrainParent);
            Transform group = groupObject.transform;
            if (DaggerfallUnity.Settings.AssetInjection)
                SpawnReplacementAwareBillboards(group, positions);
            else
                SpawnArchiveBillboards(group, positions);

            return groupObject;
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
            var animatedReplacementPositions = new List<UnderwaterDecorationPlacementInfo>();

            for (int i = 0; i < positions.Count; i++)
            {
                UnderwaterDecorationPlacementInfo item = positions[i];
                UnderwaterDecorationRecord record = item.ToRecord();

                if (UnderwaterDecorationCatalog.UsesArchiveAnimation(record))
                {
                    // DREAM-style imported multi-frame replacements animate by
                    // swapping material textures on DaggerfallBillboard. The
                    // batch paths only animate atlas UVs, which can leave these
                    // records as flat white quads.
                    UnderwaterDecorationReplacementInfo animatedInfo;
                    if (UnderwaterDecorationReplacementCache.TryGet(record, out animatedInfo))
                    {
                        if (animatedInfo.HasImportedAnimation)
                            animatedReplacementPositions.Add(item);
                        else
                            AddReplacementPosition(replacementPositions, record, item.LocalPosition);
                    }
                    else
                    {
                        AddArchivePosition(archivePositions, item);
                    }

                    continue;
                }

                UnderwaterDecorationReplacementInfo replacementInfo;
                if (!UnderwaterDecorationReplacementCache.TryGetMaterial(record, out replacementInfo))
                {
                    AddArchivePosition(archivePositions, item);
                    continue;
                }

                AddReplacementPosition(replacementPositions, record, item.LocalPosition);
            }

            if (archivePositions.Count > 0)
                SpawnArchiveBillboards(parent, archivePositions);

            foreach (KeyValuePair<UnderwaterDecorationRecord, List<Vector3>> pair in replacementPositions)
            {
                UnderwaterDecorationReplacementInfo replacementInfo;
                if (UnderwaterDecorationReplacementCache.TryGetMaterial(pair.Key, out replacementInfo))
                    SpawnReplacementBatch(parent, pair.Key, pair.Value, replacementInfo);
            }

            SpawnAnimatedReplacementBillboards(parent, animatedReplacementPositions);
        }

        private static void AddReplacementPosition(
            Dictionary<UnderwaterDecorationRecord, List<Vector3>> positionsByRecord,
            UnderwaterDecorationRecord record,
            Vector3 localPosition)
        {
            List<Vector3> recordPositions;
            if (!positionsByRecord.TryGetValue(record, out recordPositions))
            {
                recordPositions = new List<Vector3>();
                positionsByRecord.Add(record, recordPositions);
            }

            recordPositions.Add(localPosition);
        }

        private static void SpawnReplacementBatch(
            Transform parent,
            UnderwaterDecorationRecord record,
            List<Vector3> positions,
            UnderwaterDecorationReplacementInfo info)
        {
            if (parent == null || positions == null || positions.Count == 0 || info == null || info.Material == null)
                return;

            Mesh mesh = BuildMaterialBillboardBatchMesh(positions, info);
            if (mesh == null)
                return;

            var go = new GameObject("DeepWaters_DecorationMaterialBatch_" + record.Archive + "_" + record.Record);
            go.transform.parent = parent;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = info.Material;

            var owner = go.AddComponent<OwnedUnderwaterDecorationMesh>();
            owner.Set(mesh);

            ApplyUnderwaterDecorationMaterial(go.GetComponent<MeshRenderer>());
            DeepWaterRendering.DisableShadows(go);
        }

        private static void SpawnAnimatedReplacementBillboards(
            Transform parent,
            List<UnderwaterDecorationPlacementInfo> positions)
        {
            if (positions == null || positions.Count == 0)
                return;

            for (int i = 0; i < positions.Count; i++)
            {
                UnderwaterDecorationPlacementInfo item = positions[i];
                GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(
                    item.Archive,
                    item.Record,
                    parent);
                if (go == null)
                    continue;

                go.name = "DeepWaters_DecorationAnimatedReplacement_" + item.Archive + "_" + item.Record;
                go.transform.localPosition = item.LocalPosition;
                float sizeFactor = UnityEngine.Random.Range(DecorationScaleMin, DecorationScaleMax);
                go.transform.localScale = go.transform.localScale * sizeFactor;

                Billboard billboard = go.GetComponent<Billboard>();
                float framesPerSecond;
                if (billboard != null &&
                    UnderwaterDecorationCatalog.TryGetFramesPerSecond(item.Archive, out framesPerSecond))
                {
                    billboard.FramesPerSecond = Mathf.Max(1, Mathf.RoundToInt(framesPerSecond));
                }

                ApplyUnderwaterDecorationMaterial(go.GetComponent<MeshRenderer>());
                DeepWaterRendering.DisableShadows(go);
            }
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

        private static Mesh BuildMaterialBillboardBatchMesh(
            List<Vector3> positions,
            UnderwaterDecorationReplacementInfo info)
        {
            if (positions == null || positions.Count == 0 || info == null)
                return null;

            int count = positions.Count;
            var mesh = new Mesh();
            mesh.name = "DeepWaters_DecorationMaterialBatchMesh";
            if (count * 4 > 65535)
                mesh.indexFormat = IndexFormat.UInt32;

            Vector3[] vertices = new Vector3[count * 4];
            Vector3[] normals = new Vector3[count * 4];
            Vector4[] tangents = new Vector4[count * 4];
            Vector2[] uvs = new Vector2[count * 4];
            int[] triangles = new int[count * 6];

            Vector3 normal = new Vector3(0f, 0.70710678f, 0.70710678f);
            Rect rect = info.Rect;
            Bounds bounds = new Bounds();

            for (int i = 0; i < positions.Count; i++)
            {
                float sizeFactor = UnityEngine.Random.Range(DecorationScaleMin, DecorationScaleMax);
                Vector2 size = info.BatchSize * MeshReader.GlobalScale * sizeFactor;
                Vector3 center = positions[i] + Vector3.up * (size.y * 0.5f);
                int v = i * 4;
                int t = i * 6;

                vertices[v] = center;
                vertices[v + 1] = center;
                vertices[v + 2] = center;
                vertices[v + 3] = center;

                normals[v] = normal;
                normals[v + 1] = normal;
                normals[v + 2] = normal;
                normals[v + 3] = normal;

                tangents[v] = new Vector4(size.x, size.y, 0f, 1f);
                tangents[v + 1] = new Vector4(size.x, size.y, 1f, 1f);
                tangents[v + 2] = new Vector4(size.x, size.y, 0f, 0f);
                tangents[v + 3] = new Vector4(size.x, size.y, 1f, 0f);

                uvs[v] = new Vector2(rect.x, rect.yMax);
                uvs[v + 1] = new Vector2(rect.xMax, rect.yMax);
                uvs[v + 2] = new Vector2(rect.x, rect.y);
                uvs[v + 3] = new Vector2(rect.xMax, rect.y);

                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 3;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 1;

                Bounds itemBounds = new Bounds(center, new Vector3(size.x, size.y, size.x));
                if (i == 0)
                    bounds = itemBounds;
                else
                    bounds.Encapsulate(itemBounds);
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.bounds = bounds;
            return mesh;
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
        }

        private static Material CreateUnderwaterDecorationMaterial(Material sourceMaterial)
        {
            if (sourceMaterial == null)
                return null;

            if (cachedUnderwaterMaterials.Contains(sourceMaterial))
                return sourceMaterial;

            Material cached;
            if (underwaterMaterialCache.TryGetValue(sourceMaterial, out cached) && cached != null)
                return cached;

            Shader shader = LoadUnderwaterDecorationShader();
            if (shader == null)
                return null;

            var material = new Material(shader)
            {
                name = sourceMaterial.name + " (Deep Waters Underwater)",
            };

            CopyTextureAndTransform(sourceMaterial, material, Uniforms.MainTex);
            ConfigureUnderwaterDecorationMaterial(material, sourceMaterial);
            underwaterMaterialCache[sourceMaterial] = material;
            cachedUnderwaterMaterials.Add(material);
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

        private static void ConfigureUnderwaterDecorationMaterial(Material material, Material sourceMaterial = null)
        {
            if (material == null)
                return;

            material.SetOverrideTag("RenderType", TransparentCutoutRenderType);
            material.renderQueue = (int)RenderQueue.AlphaTest;

            if (material.HasProperty(ColorProperty))
                material.SetColor(ColorProperty, UnderwaterDecorationColor);

            if (material.HasProperty(CutoffProperty))
            {
                float cutoff = 0.5f;
                if (sourceMaterial != null && sourceMaterial.HasProperty(CutoffProperty))
                    cutoff = sourceMaterial.GetFloat(CutoffProperty);

                material.SetFloat(CutoffProperty, cutoff);
            }
        }

        private sealed class OwnedUnderwaterDecorationMesh : MonoBehaviour
        {
            private Mesh mesh;

            public void Set(Mesh value)
            {
                if (mesh != null && mesh != value)
                    Destroy(mesh);

                mesh = value;
            }

            private void OnDestroy()
            {
                if (mesh != null)
                    Destroy(mesh);
            }
        }

    }
}
