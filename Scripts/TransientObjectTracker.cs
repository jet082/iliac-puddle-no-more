// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepWaters
{
    internal sealed class TransientObjectTracker
    {
        private readonly List<GameObject> objects = new List<GameObject>();

        public int Count
        {
            get { return objects.Count; }
        }

        public void Add(GameObject go)
        {
            if (go != null)
                objects.Add(go);
        }

        public void Clear()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
            {
                if (objects[i] != null)
                    UnityEngine.Object.Destroy(objects[i]);
            }

            objects.Clear();
        }

        public void PruneByDistance(Vector3 playerPos, float maxFlatDistance)
        {
            Prune(playerPos, maxFlatDistance, null);
        }

        public void PruneToCount(Vector3 playerPos, int maxCount)
        {
            maxCount = Mathf.Max(0, maxCount);

            for (int i = objects.Count - 1; i >= 0; i--)
            {
                if (objects[i] == null)
                    objects.RemoveAt(i);
            }

            while (objects.Count > maxCount)
            {
                int farthestIndex = -1;
                float farthestDistanceSq = -1f;

                for (int i = 0; i < objects.Count; i++)
                {
                    GameObject go = objects[i];
                    if (go == null)
                    {
                        farthestIndex = i;
                        break;
                    }

                    Vector3 delta = go.transform.position - playerPos;
                    delta.y = 0f;
                    float distanceSq = delta.sqrMagnitude;
                    if (distanceSq > farthestDistanceSq)
                    {
                        farthestDistanceSq = distanceSq;
                        farthestIndex = i;
                    }
                }

                if (farthestIndex < 0)
                    return;

                GameObject remove = objects[farthestIndex];
                if (remove != null)
                    UnityEngine.Object.Destroy(remove);

                objects.RemoveAt(farthestIndex);
            }
        }

        public void Prune(Vector3 playerPos, float maxFlatDistance, Predicate<GameObject> shouldKeep)
        {
            float maxFlatDistanceSq = maxFlatDistance * maxFlatDistance;

            for (int i = objects.Count - 1; i >= 0; i--)
            {
                GameObject go = objects[i];
                if (go == null)
                {
                    objects.RemoveAt(i);
                    continue;
                }

                Vector3 delta = go.transform.position - playerPos;
                delta.y = 0f;
                if (delta.sqrMagnitude > maxFlatDistanceSq || (shouldKeep != null && !shouldKeep(go)))
                {
                    UnityEngine.Object.Destroy(go);
                    objects.RemoveAt(i);
                }
            }
        }
    }
}
