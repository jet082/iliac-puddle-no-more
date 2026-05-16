// Project:         Iliac Puddle No More
// License:         MIT

using UnityEngine;

namespace DeepWaters
{
    internal class PassiveFishSchool
    {
        private const float CruiseSpeed = 0.95f;
        private const float DisruptedSpeed = 1.45f;
        private const float DirectionHoldMin = 2.2f;
        private const float DirectionHoldMax = 4.4f;
        private const float DisruptedMemorySeconds = 3.0f;
        private const float SeafloorClearance = 1.2f;
        private const float SurfaceClearance = 1.4f;

        private Vector3 center;
        private Vector3 cruiseDirection;
        private Vector3 disruptedDirection;
        private float radius;
        private float cruiseSpeedMultiplier;
        private float fleeSpeedMultiplier;
        private float nextDirectionTime;
        private float disruptedUntil;
        private int lastUpdateFrame = -1;

        public PassiveFishSchool(Vector3 startCenter, float schoolRadius, float cruiseMultiplier, float fleeMultiplier)
        {
            center = startCenter;
            radius = Mathf.Max(0.1f, schoolRadius);
            cruiseSpeedMultiplier = cruiseMultiplier;
            fleeSpeedMultiplier = fleeMultiplier;
            PickCruiseDirection();
            disruptedDirection = cruiseDirection;
        }

        public Vector3 Center
        {
            get { return center; }
        }

        public float Radius
        {
            get { return radius; }
        }

        public Vector3 CurrentDirection
        {
            get { return IsDisrupted ? disruptedDirection : cruiseDirection; }
        }

        public bool IsDisrupted
        {
            get { return Time.time < disruptedUntil; }
        }

        public void ReportThreat(Vector3 awayFromPlayer)
        {
            awayFromPlayer.y *= 0.25f;
            if (awayFromPlayer.sqrMagnitude < 0.01f)
                return;

            disruptedDirection = awayFromPlayer.normalized;
            disruptedUntil = Time.time + DisruptedMemorySeconds;
        }

        public Vector3 GetFleeDirection(Vector3 fishPosition, Vector3 playerPosition)
        {
            Vector3 away = fishPosition - playerPosition;
            away.y *= 0.25f;
            if (away.sqrMagnitude > 0.01f)
                return away.normalized;

            return disruptedDirection.sqrMagnitude > 0.01f ? disruptedDirection : cruiseDirection;
        }

        public void Update()
        {
            if (lastUpdateFrame == Time.frameCount)
                return;

            lastUpdateFrame = Time.frameCount;
            if (!IsDisrupted && Time.time >= nextDirectionTime)
                PickCruiseDirection();

            Vector3 direction = CurrentDirection;
            if (direction.sqrMagnitude < 0.01f)
                return;

            float speed = IsDisrupted ? DisruptedSpeed * fleeSpeedMultiplier : CruiseSpeed * cruiseSpeedMultiplier;
            MoveCenter(direction.normalized, speed);
        }

        private void PickCruiseDirection()
        {
            cruiseDirection = Random.insideUnitSphere;
            cruiseDirection.y *= 0.15f;
            if (cruiseDirection.sqrMagnitude < 0.01f)
                cruiseDirection = Vector3.forward;

            cruiseDirection.Normalize();
            nextDirectionTime = Time.time + Random.Range(DirectionHoldMin, DirectionHoldMax);
        }

        private void MoveCenter(Vector3 direction, float speed)
        {
            Vector3 nextCenter = center + direction * speed * Time.deltaTime;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(nextCenter.x, nextCenter.z, out column) || column.Depth < 2f)
            {
                cruiseDirection = -cruiseDirection;
                disruptedDirection = -disruptedDirection;
                nextDirectionTime = Time.time + 1f;
                return;
            }

            float minY = column.SeafloorWorldY + SeafloorClearance;
            float maxY = column.OceanWorldY - SurfaceClearance;

            if (nextCenter.y < minY)
            {
                nextCenter.y = minY;
                cruiseDirection.y = Mathf.Abs(cruiseDirection.y);
                disruptedDirection.y = Mathf.Abs(disruptedDirection.y);
            }
            else if (nextCenter.y > maxY)
            {
                nextCenter.y = maxY;
                cruiseDirection.y = -Mathf.Abs(cruiseDirection.y);
                disruptedDirection.y = -Mathf.Abs(disruptedDirection.y);
            }

            center = nextCenter;
        }
    }
}

