// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
    internal static class PassiveFishFactory
    {
        public static GameObject Spawn(Vector3 worldPos, Transform parent, PassiveFishSpecies species, PassiveFishSchool school)
        {
            if (species == null || !PassiveFishResources.LoadFishTexture(species))
                return null;

            DaggerfallUnityItem fishItem;
            if (!PassiveFishResources.TryCreateFishItem(species, out fishItem))
                return null;

            GameObject go = new GameObject("DeepWaters " + fishItem.shortName);
            if (parent != null)
                go.transform.parent = parent;

            float height = species.BillboardHeight * Random.Range(species.MinHeightMultiplier, species.MaxHeightMultiplier);
            Vector2 billboardSize = GetBillboardSize(species, height);

            DaggerfallBillboard billboard = go.AddComponent<DaggerfallBillboard>();
            billboard.FaceY = true;

            Material material = billboard.SetMaterial(species.Texture, billboardSize);
            if (material == null)
            {
                Object.Destroy(go);
                return null;
            }

            if (material.HasProperty("_Cutoff"))
                material.SetFloat("_Cutoff", 0.1f);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            UnderwaterDecorationBatchFactory.ApplyUnderwaterDecorationMaterial(renderer);

            go.transform.position = worldPos;
            DeepWaterRendering.FaceMainCamera(go.transform);
            AddClickCollider(go, billboardSize);
            DeepWaterRendering.DisableShadows(renderer);

            DaggerfallLoot loot = go.AddComponent<DaggerfallLoot>();
            loot.ContainerType = LootContainerTypes.DroppedLoot;
            loot.TextureArchive = species.TextureArchive;
            loot.TextureRecord = species.TextureRecord;
            loot.Items.AddItem(fishItem);

            FishLootIcon lootIcon = go.AddComponent<FishLootIcon>();
            lootIcon.Texture = PassiveFishResources.GetFishIconTexture(species);

            PassiveFishBehaviour behaviour = go.AddComponent<PassiveFishBehaviour>();
            behaviour.Initialize(
                loot,
                species.CruiseSpeedMultiplier,
                species.FleeSpeedMultiplier,
                school,
                species.FleeDartHoldMin,
                species.FleeDartHoldMax);

            return go;
        }

        private static Vector2 GetBillboardSize(PassiveFishSpecies species, float height)
        {
            float aspect = species.Texture != null && species.Texture.height > 0
                ? (float)species.Texture.width / species.Texture.height
                : 1.8f;

            return new Vector2(height * aspect, height);
        }

        private static void AddClickCollider(GameObject go, Vector2 billboardSize)
        {
            BoxCollider clickCollider = go.AddComponent<BoxCollider>();
            clickCollider.isTrigger = true;
            clickCollider.size = new Vector3(
                billboardSize.x,
                billboardSize.y,
                Mathf.Max(0.35f, billboardSize.x * 0.25f));
        }
    }
}
