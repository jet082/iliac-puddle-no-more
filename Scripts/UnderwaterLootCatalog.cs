// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    internal static class UnderwaterLootCatalog
    {
        // Archive 216 records 42-48 are used by passive fish item icons in this
        // mod, so treasure containers deliberately avoid those records.
        private static readonly int[] TreasurePileRecords = { 0, 1, 3, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40 };
		private static readonly UnderwaterDecorationRecord[] RubbleRecords =
		{
			R(105, 0), R(105, 5), R(105, 6), R(105, 7), R(105, 8), R(105, 9), R(105, 10),
			R(400, 0), R(400, 1), R(400, 4), R(400, 6),
			R(380, 1),
			R(96, 0), R(96, 2), R(96, 3), R(96, 4), R(96, 5),
		};

        public static int PickTreasurePileRecord()
        {
            return TreasurePileRecords[Random.Range(0, TreasurePileRecords.Length)];
        }

        public static UnderwaterDecorationRecord PickRubbleRecord()
        {
            return RubbleRecords[Random.Range(0, RubbleRecords.Length)];
        }

        public static UnderwaterDecorationRecord PickRubbleRecordExcept(HashSet<UnderwaterDecorationRecord> excludedRecords)
        {
            if (excludedRecords == null || excludedRecords.Count >= RubbleRecords.Length)
                return PickRubbleRecord();

            UnderwaterDecorationRecord record;
            do
            {
                record = PickRubbleRecord();
            }
            while (excludedRecords.Contains(record));

            return record;
        }

		private static UnderwaterDecorationRecord R(int archive, int record)
		{
			return new UnderwaterDecorationRecord(archive, record);
		}

        public static void FillRandomItem(DaggerfallLoot loot)
        {
            if (loot == null)
                return;

            var pe = GameManager.Instance.PlayerEntity;
            int level = pe != null ? pe.Level : 1;
            var gender = pe != null ? pe.Gender : Genders.Male;
            var race = pe != null ? pe.Race : Races.Breton;

            DaggerfallUnityItem item = null;
            float roll = Random.value;
            if (roll < 0.25f)       item = ItemBuilder.CreateRandomReligiousItem();
            else if (roll < 0.45f)  item = ItemBuilder.CreateRandomPotion();
            else if (roll < 0.60f)  item = ItemBuilder.CreateRandomJewellery();
            else if (roll < 0.75f)  item = ItemBuilder.CreateRandomGem();
            else if (roll < 0.85f)  item = ItemBuilder.CreateRandomClothing(gender, race);
            else if (roll < 0.95f)  item = ItemBuilder.CreateRandomWeapon(level);
            else                    item = ItemBuilder.CreateRandomArmor(level, gender, race);

            if (item != null)
                loot.Items.AddItem(item);
        }
    }
}
