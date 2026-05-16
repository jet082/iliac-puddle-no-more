// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepWaters
{
    internal struct UnderwaterDecorationRecord : IEquatable<UnderwaterDecorationRecord>
    {
        public readonly int Archive;
        public readonly int Record;

        public UnderwaterDecorationRecord(int archive, int record)
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
        public readonly int Archive;
        public readonly int Record;
        public readonly Vector3 LocalPosition;

        public UnderwaterDecorationPlacementInfo(UnderwaterDecorationRecord record, Vector3 localPosition)
        {
            Archive = record.Archive;
            Record = record.Record;
            LocalPosition = localPosition;
        }

        public UnderwaterDecorationRecord ToRecord()
        {
            return new UnderwaterDecorationRecord(Archive, Record);
        }
    }

    internal static class UnderwaterDecorationCatalog
    {
        public const int Archive = 105;
        private const int GeneralPoolWeight = 120;
        private const int AnimatedGeneralPoolBonusWeight = 30;
        private const int DebrisPoolWeight = 4;
        private const float Texture106FramesPerSecond = 5f;

        private static readonly UnderwaterDecorationRecord[] WeightedRecords = BuildWeightedRecords();

        public static UnderwaterDecorationRecord PickRecord()
        {
            return WeightedRecords[UnityEngine.Random.Range(0, WeightedRecords.Length)];
        }

        public static bool TryGetFramesPerSecond(int archive, out float framesPerSecond)
        {
            if (archive == 106)
            {
                framesPerSecond = Texture106FramesPerSecond;
                return true;
            }

            framesPerSecond = 0f;
            return false;
        }

        public static bool UsesArchiveAnimation(UnderwaterDecorationRecord record)
        {
            return record.Archive == 106 && record.Record >= 2 && record.Record <= 6;
        }

        private static UnderwaterDecorationRecord[] BuildWeightedRecords()
        {
            UnderwaterDecorationRecord[] general =
            {
                R(105, 1), R(105, 2), R(105, 3), R(105, 4),
                R(106, 2), R(106, 3), R(106, 4), R(106, 5), R(106, 6),
                R(211, 8), R(211, 9), R(211, 10),
                R(213, 11), R(213, 12), R(213, 15),
                R(253, 23), R(253, 24),
                R(501, 18), R(501, 21), R(501, 22), R(501, 23),
                R(501, 26), R(501, 29), R(501, 31),
                R(502, 1), R(502, 2), R(502, 3), R(502, 4), R(502, 5),
                R(502, 6), R(502, 7), R(502, 8), R(502, 9), R(502, 10), R(502, 11),
                R(502, 21), R(502, 22), R(502, 23),
                R(502, 26), R(502, 27), R(502, 28), R(502, 29),
                R(502, 31),
            };

            UnderwaterDecorationRecord[] debris =
            {
                R(105, 0), R(105, 5), R(105, 6), R(105, 7), R(105, 8), R(105, 9), R(105, 10),
                R(106, 0), R(106, 1),
                R(206, 0), R(206, 1), R(206, 3), R(206, 4), R(206, 5), R(206, 6),
                R(206, 8), R(206, 29), R(206, 30), R(206, 31), R(206, 32),
                R(253, 16), R(253, 54), R(253, 55),
                R(305, 0), R(305, 1), R(305, 2),
                R(306, 0),
            };

            var records = new List<UnderwaterDecorationRecord>();
            for (int i = 0; i < general.Length; i++)
            {
                int weight = GeneralPoolWeight;
                if (UsesArchiveAnimation(general[i]))
                    weight += AnimatedGeneralPoolBonusWeight;

                for (int n = 0; n < weight; n++)
                    records.Add(general[i]);
            }

            for (int i = 0; i < debris.Length; i++)
            {
                for (int n = 0; n < DebrisPoolWeight; n++)
                    records.Add(debris[i]);
            }

            return records.ToArray();
        }

        private static UnderwaterDecorationRecord R(int archive, int record)
        {
            return new UnderwaterDecorationRecord(archive, record);
        }
    }
}
