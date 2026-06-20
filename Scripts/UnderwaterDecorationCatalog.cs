// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepWaters
{
    internal struct UnderwaterDecorationRecord : IEquatable<UnderwaterDecorationRecord>
    {
        internal readonly int Archive;
        internal readonly int Record;

        internal UnderwaterDecorationRecord(int archive, int record)
        {
            Archive = archive;
            Record = record;
        }

        public bool Equals(UnderwaterDecorationRecord other)
        {
            return Archive == other.Archive && Record == other.Record;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is UnderwaterDecorationRecord))
                return false;

            return Equals((UnderwaterDecorationRecord)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Archive * 397) ^ Record;
            }
        }
    }

    internal struct UnderwaterDecorationPlacementInfo
    {
        internal readonly int Archive;
        internal readonly int Record;
        internal readonly Vector3 LocalPosition;

        internal UnderwaterDecorationPlacementInfo(UnderwaterDecorationRecord record, Vector3 localPosition)
        {
            Archive = record.Archive;
            Record = record.Record;
            LocalPosition = localPosition;
        }

        internal UnderwaterDecorationRecord ToRecord()
        {
            return new UnderwaterDecorationRecord(Archive, Record);
        }
    }

	internal static class UnderwaterDecorationCatalog
	{
		private const float Texture106FramesPerSecond = 5f;

		private static readonly UnderwaterDecorationRecord[] OpenOceanPool = BuildOpenOceanPool();
		private static readonly UnderwaterDecorationRecord[] TropicalPool = BuildTropicalPool();
		private static readonly UnderwaterDecorationRecord[] TemperatePool = BuildTemperatePool();
		private static readonly UnderwaterDecorationRecord[] SwampPool = BuildSwampPool();
		private static readonly UnderwaterDecorationRecord[] ColdPool = BuildColdPool();
		private static readonly UnderwaterDecorationRecord[] DesertPool = BuildDesertPool();

        internal static UnderwaterDecorationRecord PickRecord(int climateIndex)
        {
            return PickRecordForBiome(PassiveFishSpeciesCatalog.ClimateToBiome(climateIndex));
        }

		private static UnderwaterDecorationRecord PickRecordForBiome(WaterBiome biome)
		{
			UnderwaterDecorationRecord[] pool = PoolForBiome(biome);
			return pool[UnityEngine.Random.Range(0, pool.Length)];
		}

		private static UnderwaterDecorationRecord[] PoolForBiome(WaterBiome biome)
		{
			switch (biome)
			{
				case WaterBiome.Tropical:  return TropicalPool;
				case WaterBiome.Temperate: return TemperatePool;
				case WaterBiome.Swamp:     return SwampPool;
				case WaterBiome.Cold:      return ColdPool;
				case WaterBiome.Desert:    return DesertPool;
				default:                   return OpenOceanPool;
			}
		}

        internal static bool TryGetFramesPerSecond(int archive, out float framesPerSecond)
        {
            if (archive == 106)
            {
                framesPerSecond = Texture106FramesPerSecond;
                return true;
            }

            framesPerSecond = 0f;
            return false;
        }

        internal static bool UsesArchiveAnimation(UnderwaterDecorationRecord record)
        {
            return record.Archive == 106 && record.Record >= 2 && record.Record <= 6;
        }

		private static UnderwaterDecorationRecord[] BuildOpenOceanPool()
		{
			var records = new List<UnderwaterDecorationRecord>();
			AddCommonWater(records);
			Add(records, 106, 2, 12);
			Add(records, 106, 3, 10);
			Add(records, 211, 9, 10);
			Add(records, 211, 10, 6);
			Add(records, 253, 24, 8);
			Add(records, 501, 29, 8);
			Add(records, 502, 2, 8);
			Add(records, 105, 5, 8);
			Add(records, 105, 6, 8);
			Add(records, 105, 7, 8);
			Add(records, 105, 8, 8);
			Add(records, 105, 9, 8);
			Add(records, 105, 10, 8);
			Add(records, 206, 29, 3);
			Add(records, 206, 30, 3);
			Add(records, 206, 31, 3);
			Add(records, 206, 32, 3);
			AddDeadSeaLife(records, 3);
			return records.ToArray();
		}

		private static UnderwaterDecorationRecord[] BuildTropicalPool()
		{
			var records = new List<UnderwaterDecorationRecord>();
			AddCommonWater(records);
			AddMany(records, 12, R(105, 2), R(105, 3), R(105, 4));
			AddMany(records, 12, R(211, 9), R(211, 10));
			AddMany(records, 16, R(213, 15));
			AddMany(records, 12, R(253, 63));
			AddMany(records, 16, R(501, 1), R(501, 18), R(501, 21), R(501, 23), R(501, 26), R(501, 29), R(501, 31));
			AddMany(records, 14, R(502, 1), R(502, 2), R(502, 11), R(502, 21), R(502, 22), R(502, 27), R(502, 28), R(502, 29), R(502, 31));
			Add(records, 105, 0, 2);
			Add(records, 105, 5, 2);
			AddDeadSeaLife(records, 1);
			return records.ToArray();
		}

		private static UnderwaterDecorationRecord[] BuildTemperatePool()
		{
			var records = new List<UnderwaterDecorationRecord>();
			AddCommonWater(records);
			AddMany(records, 14, R(106, 2), R(106, 3), R(106, 4), R(106, 5));
			Add(records, 105, 1, 10);
			AddMany(records, 12, R(213, 11), R(213, 12), R(213, 15));
			AddMany(records, 12, R(501, 18), R(501, 21), R(501, 23), R(501, 26), R(501, 29), R(501, 31));
			AddMany(records, 10, R(502, 7), R(502, 8), R(502, 21), R(502, 23), R(502, 26), R(502, 27), R(502, 28), R(502, 31));
			AddMany(records, 6, R(211, 9), R(211, 10), R(253, 24));
			Add(records, 105, 5, 3);
			Add(records, 105, 6, 3);
			AddDeadSeaLife(records, 1);
			return records.ToArray();
		}

		private static UnderwaterDecorationRecord[] BuildSwampPool()
		{
			var records = new List<UnderwaterDecorationRecord>();
			AddCommonWater(records);
			Add(records, 105, 1, 10);
			AddMany(records, 16, R(106, 4), R(106, 5));
			Add(records, 211, 10, 6);
			AddMany(records, 14, R(213, 15));
			AddMany(records, 16, R(502, 3), R(502, 4), R(502, 5), R(502, 6), R(502, 7), R(502, 8), R(502, 9), R(502, 10), R(502, 21), R(502, 22), R(502, 29), R(502, 30));
			AddMany(records, 10, R(501, 18), R(501, 21), R(501, 23), R(501, 26), R(501, 27), R(501, 28), R(501, 29));
			Add(records, 105, 5, 4);
			Add(records, 105, 10, 4);
			AddDeadSeaLife(records, 2);
			return records.ToArray();
		}

		private static UnderwaterDecorationRecord[] BuildColdPool()
		{
			var records = new List<UnderwaterDecorationRecord>();
			AddCommonWater(records);
			Add(records, 106, 6, 14);
			AddMany(records, 10, R(206, 0), R(206, 1), R(206, 3), R(206, 4), R(206, 5), R(206, 6));
			AddMany(records, 8, R(105, 5), R(105, 6), R(105, 7), R(105, 8), R(105, 9), R(105, 10));
			AddMany(records, 7, R(206, 29), R(206, 30), R(206, 31), R(206, 32));
			AddMany(records, 5, R(211, 10), R(253, 24), R(502, 4), R(502, 5), R(502, 6), R(502, 11));
			AddMany(records, 3, R(206, 0), R(206, 1), R(206, 29), R(206, 30), R(206, 31), R(206, 32));
			AddDeadSeaLife(records, 4);
			return records.ToArray();
		}

		private static UnderwaterDecorationRecord[] BuildDesertPool()
		{
			var records = new List<UnderwaterDecorationRecord>();
			AddCommonWater(records);
			Add(records, 211, 10, 6);
			AddMany(records, 8, R(105, 3), R(105, 4), R(105, 9));
			AddMany(records, 4, R(502, 4), R(502, 5), R(502, 6), R(502, 11));
			Add(records, 305, 1, 4);
			return records.ToArray();
		}

		private static void AddCommonWater(List<UnderwaterDecorationRecord> records)
		{
			Add(records, 106, 6, 8);
			Add(records, 106, 2, 3);
			Add(records, 106, 3, 3);
		}

		private static void AddDeadSeaLife(List<UnderwaterDecorationRecord> records, int weight)
		{
			AddMany(records, weight, R(305, 0), R(305, 1), R(305, 2), R(306, 0), R(380, 1));
		}

		private static void AddMany(List<UnderwaterDecorationRecord> records, int weight, params UnderwaterDecorationRecord[] items)
		{
			for (int i = 0; i < items.Length; i++)
				Add(records, items[i], weight);
		}

		private static void Add(List<UnderwaterDecorationRecord> records, int archive, int record, int weight)
		{
			Add(records, R(archive, record), weight);
		}

		private static void Add(List<UnderwaterDecorationRecord> records, UnderwaterDecorationRecord record, int weight)
		{
			for (int i = 0; i < weight; i++)
				records.Add(record);
		}

		private static UnderwaterDecorationRecord R(int archive, int record)
        {
            return new UnderwaterDecorationRecord(archive, record);
        }
    }
}
