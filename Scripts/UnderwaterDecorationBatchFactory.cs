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
            var archivePositions = new Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>>();
            var animatedPositions = new Dictionary<int, List<UnderwaterDecorationPlacementInfo>>();
            for (int i = 0; i < positions.Count; i++)
                AddArchiveOrAnimatedPosition(archivePositions, animatedPositions, positions[i]);

            if (archivePositions.Count > 0)
                SpawnArchiveBillboards(parent, archivePositions);

            if (animatedPositions.Count > 0)
                SpawnAnimatedArchiveBillboards(parent, animatedPositions);
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

#pragma warning disable 0618
            for (int i = 0; i < positions.Count; i++)
                batch.AddItem(positions[i]);
#pragma warning restore 0618

            batch.Apply();
            DeepWaterRendering.DisableShadows(batch.gameObject);
        }

        private static void SpawnReplacementAwareBillboards(Transform parent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            var archivePositions = new Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>>();
            var animatedPositions = new Dictionary<int, List<UnderwaterDecorationPlacementInfo>>();
            var replacementPositions = new Dictionary<UnderwaterDecorationRecord, List<Vector3>>();

            for (int i = 0; i < positions.Count; i++)
            {
                UnderwaterDecorationPlacementInfo item = positions[i];
                UnderwaterDecorationRecord record = item.ToRecord();
                if (UnderwaterDecorationCatalog.UsesArchiveAnimation(record))
                {
                    AddAnimatedPosition(animatedPositions, item);
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

            if (animatedPositions.Count > 0)
                SpawnAnimatedArchiveBillboards(parent, animatedPositions);

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

#pragma warning disable 0618
            for (int i = 0; i < positions.Count; i++)
            {
                float sizeFactor = Random.Range(DecorationScaleMin, DecorationScaleMax);
                float scaleValue = (sizeFactor - 1f) * BlocksFile.ScaleDivisor;
                Vector2 scale = new Vector2(scaleValue, scaleValue);
                batch.AddItem(info.Rect, info.BatchSize, scale, positions[i]);
            }
#pragma warning restore 0618

            batch.Apply();
            DeepWaterRendering.DisableShadows(batch.gameObject);
        }

        private static void SpawnAnimatedArchiveBillboards(
            Transform parent,
            Dictionary<int, List<UnderwaterDecorationPlacementInfo>> archivePositions)
        {
            foreach (KeyValuePair<int, List<UnderwaterDecorationPlacementInfo>> pair in archivePositions)
                SpawnAnimatedArchiveBillboards(parent, pair.Key, pair.Value);
        }

        private static void SpawnAnimatedArchiveBillboards(
            Transform parent,
            int archive,
            List<UnderwaterDecorationPlacementInfo> positions)
        {
            if (positions == null || positions.Count == 0)
                return;

            GameObject go = new GameObject("DeepWaters_AnimatedDecorationArchiveBatch_" + archive);
            go.transform.parent = parent;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var batch = go.AddComponent<AnimatedDecorationBatch>();
            if (!batch.Initialize(archive, positions))
            {
                Object.Destroy(go);
                return;
            }

            DeepWaterRendering.DisableShadows(go);
        }

        private static void AddArchiveOrAnimatedPosition(
            Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>> archivePositions,
            Dictionary<int, List<UnderwaterDecorationPlacementInfo>> animatedPositions,
            UnderwaterDecorationPlacementInfo item)
        {
            if (UnderwaterDecorationCatalog.UsesArchiveAnimation(item.ToRecord()))
                AddAnimatedPosition(animatedPositions, item);
            else
                AddArchivePosition(archivePositions, item);
        }

        private static void AddAnimatedPosition(
            Dictionary<int, List<UnderwaterDecorationPlacementInfo>> animatedPositions,
            UnderwaterDecorationPlacementInfo item)
        {
            List<UnderwaterDecorationPlacementInfo> positions;
            if (!animatedPositions.TryGetValue(item.Archive, out positions))
            {
                positions = new List<UnderwaterDecorationPlacementInfo>();
                animatedPositions.Add(item.Archive, positions);
            }

            positions.Add(item);
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

        private sealed class AnimatedDecorationBatch : MonoBehaviour
        {
            private const int VertsPerQuad = 4;
            private const int IndicesPerQuad = 6;

            private Mesh mesh;
            private Rect[] atlasRects;
            private RecordIndex[] atlasIndices;
            private int[] records;
            private int[] currentFrames;
            private int[] frameCounts;
            private Vector2[] uvs;
            private float framesPerSecond;
            private float nextFrameTime;
            private Material runtimeMaterial;

            public bool Initialize(int archive, List<UnderwaterDecorationPlacementInfo> positions)
            {
                DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
                if (dfUnity == null || !dfUnity.IsReady || dfUnity.MaterialReader == null ||
                    positions == null || positions.Count == 0)
                {
                    return false;
                }

                int atlasSize = DaggerfallUnity.Settings.AssetInjection ? 4096 : 2048;
                Material sourceMaterial = dfUnity.MaterialReader.GetMaterialAtlas(
                    archive,
                    0,
                    4,
                    atlasSize,
                    out atlasRects,
                    out atlasIndices,
                    4,
                    true,
                    0,
                    false,
                    true);
                if (sourceMaterial == null || atlasRects == null || atlasIndices == null)
                    return false;

                CachedMaterial cachedMaterial;
                if (!dfUnity.MaterialReader.GetCachedMaterialAtlas(archive, out cachedMaterial) ||
                    cachedMaterial.recordSizes == null ||
                    cachedMaterial.recordScales == null ||
                    cachedMaterial.atlasFrameCounts == null)
                {
                    return false;
                }

                runtimeMaterial = CreateBatchMaterial(sourceMaterial);
                if (runtimeMaterial == null)
                    return false;

                MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
                MeshFilter meshFilter = gameObject.AddComponent<MeshFilter>();
                meshRenderer.sharedMaterial = runtimeMaterial;
                meshRenderer.receiveShadows = false;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;

                BuildMesh(positions, cachedMaterial);
                if (mesh == null)
                    return false;

                meshFilter.sharedMesh = mesh;

                if (!UnderwaterDecorationCatalog.TryGetFramesPerSecond(archive, out framesPerSecond))
                    framesPerSecond = 5f;
                framesPerSecond = Mathf.Max(1f, framesPerSecond);
                nextFrameTime = Time.time + (1f / framesPerSecond);
                return true;
            }

            private void OnDestroy()
            {
                if (mesh != null)
                    Destroy(mesh);

                if (runtimeMaterial != null)
                    Destroy(runtimeMaterial);
            }

            private static Material CreateBatchMaterial(Material sourceMaterial)
            {
                Shader shader = Shader.Find(MaterialReader._DaggerfallBillboardBatchNoShadowsShaderName);
                if (shader == null)
                    return null;

                Material material = new Material(shader);
                material.mainTexture = sourceMaterial.mainTexture;
                return material;
            }

            private void BuildMesh(
                List<UnderwaterDecorationPlacementInfo> positions,
                CachedMaterial cachedMaterial)
            {
                int count = positions.Count;
                var vertices = new Vector3[count * VertsPerQuad];
                var normals = new Vector3[vertices.Length];
                var tangents = new Vector4[vertices.Length];
                uvs = new Vector2[vertices.Length];
                var indices = new int[count * IndicesPerQuad];
                records = new int[count];
                currentFrames = new int[count];
                frameCounts = new int[count];

                Bounds bounds = new Bounds();
                bool hasBounds = false;
                Vector3 normal = Vector3.Normalize(Vector3.up + Vector3.forward);

                for (int i = 0; i < count; i++)
                {
                    UnderwaterDecorationPlacementInfo item = positions[i];
                    int record = item.Record;
                    if (record < 0 ||
                        record >= cachedMaterial.recordSizes.Length ||
                        record >= cachedMaterial.recordScales.Length ||
                        record >= cachedMaterial.atlasFrameCounts.Length ||
                        record >= atlasIndices.Length)
                    {
                        continue;
                    }

                    Vector2 size = GetScaledBillboardSize(
                        cachedMaterial.recordSizes[record],
                        cachedMaterial.recordScales[record]);
                    Vector3 origin = item.LocalPosition + new Vector3(0f, size.y * 0.5f, 0f);
                    int frameCount = Mathf.Max(1, cachedMaterial.atlasFrameCounts[record]);
                    int startFrame = frameCount > 1 ? Random.Range(0, frameCount) : 0;

                    records[i] = record;
                    currentFrames[i] = startFrame;
                    frameCounts[i] = frameCount;

                    int vi = i * VertsPerQuad;
                    vertices[vi] = origin;
                    vertices[vi + 1] = origin;
                    vertices[vi + 2] = origin;
                    vertices[vi + 3] = origin;

                    normals[vi] = normal;
                    normals[vi + 1] = normal;
                    normals[vi + 2] = normal;
                    normals[vi + 3] = normal;

                    tangents[vi] = new Vector4(size.x, size.y, 0f, 1f);
                    tangents[vi + 1] = new Vector4(size.x, size.y, 1f, 1f);
                    tangents[vi + 2] = new Vector4(size.x, size.y, 0f, 0f);
                    tangents[vi + 3] = new Vector4(size.x, size.y, 1f, 0f);

                    SetQuadUvs(i, record, startFrame);

                    int ii = i * IndicesPerQuad;
                    indices[ii] = vi;
                    indices[ii + 1] = vi + 1;
                    indices[ii + 2] = vi + 2;
                    indices[ii + 3] = vi + 3;
                    indices[ii + 4] = vi + 2;
                    indices[ii + 5] = vi + 1;

                    Bounds quadBounds = new Bounds(
                        origin,
                        new Vector3(size.x, size.y, size.x));
                    if (!hasBounds)
                    {
                        bounds = quadBounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(quadBounds);
                    }
                }

                mesh = new Mesh { name = "DeepWaters_AnimatedDecorationBatchMesh" };
                if (vertices.Length > 65535)
                    mesh.indexFormat = IndexFormat.UInt32;

                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.tangents = tangents;
                mesh.uv = uvs;
                mesh.triangles = indices;
                if (hasBounds)
                    mesh.bounds = bounds;
            }

            private static Vector2 GetScaledBillboardSize(Vector2 size, Vector2 scale)
            {
                int xChange = (int)(size.x * (scale.x / BlocksFile.ScaleDivisor));
                int yChange = (int)(size.y * (scale.y / BlocksFile.ScaleDivisor));
                return new Vector2(size.x + xChange, size.y + yChange) * MeshReader.GlobalScale;
            }

            private void Update()
            {
                if (mesh == null || records == null || Time.time < nextFrameTime)
                    return;

                float frameDuration = 1f / Mathf.Max(1f, framesPerSecond);
                nextFrameTime = Time.time + frameDuration;

                bool changed = false;
                for (int i = 0; i < records.Length; i++)
                {
                    if (frameCounts[i] <= 1)
                        continue;

                    currentFrames[i]++;
                    if (currentFrames[i] >= frameCounts[i])
                        currentFrames[i] = 0;

                    SetQuadUvs(i, records[i], currentFrames[i]);
                    changed = true;
                }

                if (changed)
                    mesh.uv = uvs;
            }

            private void SetQuadUvs(int billboardIndex, int record, int frame)
            {
                RecordIndex index = atlasIndices[record];
                Rect rect = atlasRects[index.startIndex + frame];
                int vi = billboardIndex * VertsPerQuad;

                uvs[vi] = new Vector2(rect.x, rect.yMax);
                uvs[vi + 1] = new Vector2(rect.xMax, rect.yMax);
                uvs[vi + 2] = new Vector2(rect.x, rect.y);
                uvs[vi + 3] = new Vector2(rect.xMax, rect.y);
            }
        }
    }
}
