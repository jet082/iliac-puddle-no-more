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
