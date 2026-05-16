// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
    public class PassiveFishBehaviour : MonoBehaviour
    {
        private const float BaseCruiseSpeed = 1.2f;
        private const float BaseFleeSpeed = 3.5f;
        private const float FleeDistance = 8.0f;
        private const float TurnIntervalMin = 5.0f;
        private const float TurnIntervalMax = 9.0f;
        private const float CruiseTurnSharpness = 0.75f;
        private const float SchoolCruiseTurnSharpness = 2.0f;
        private const float SchoolCohesionWeight = 0.35f;
        private const float SchoolReturnCohesionWeight = 1.25f;
        private const float FleeTurnSharpness = 9.0f;
        private const float FleeDartAngleMin = 35f;
        private const float FleeDartAngleMax = 75f;
        private const float DefaultFleeDartHoldMin = 1.6f;
        private const float DefaultFleeDartHoldMax = 2.8f;
        private const float FleeDartVerticalBias = 0.18f;
        private const float SeafloorClearance = 0.8f;
        private const float SurfaceClearance = 1.4f;
        private const float WaterColumnRefreshInterval = 0.25f;

        private DaggerfallLoot loot;
        private Vector3 swimDirection;
        private Vector3 targetDirection;
        private Vector3 fleeDartDirection;
        private Vector3 lastSafePosition;
        private Vector3 schoolOffset;
        private float nextTurnTime;
        private float nextFleeDartTime;
        private bool hasLastSafePosition;
        private float cruiseSpeedMultiplier;
        private float fleeSpeedMultiplier;
        private PassiveFishSchool school;
        private float fleeDartHoldMin = DefaultFleeDartHoldMin;
        private float fleeDartHoldMax = DefaultFleeDartHoldMax;
        private DeepWaterColumn cachedColumn;
        private float nextWaterColumnRefreshTime;
        private bool hasCachedColumn;

        internal void Initialize(DaggerfallLoot lootTarget, float cruiseMultiplier, float fleeMultiplier, PassiveFishSchool fishSchool, float dartHoldMin, float dartHoldMax)
        {
            loot = lootTarget;
            cruiseSpeedMultiplier = cruiseMultiplier;
            fleeSpeedMultiplier = fleeMultiplier;
            school = fishSchool;
            fleeDartHoldMin = Mathf.Max(0.1f, dartHoldMin);
            fleeDartHoldMax = Mathf.Max(fleeDartHoldMin, dartHoldMax);
            if (school != null)
                schoolOffset = transform.position - school.Center;

            lastSafePosition = transform.position;
            hasLastSafePosition = true;
            PickWanderDirection();
        }

        void Update()
        {
            if (loot != null && loot.Items.Count == 0)
            {
                Destroy(gameObject);
                return;
            }

            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null)
                return;

            Vector3 playerPos = gameManager.PlayerObject.transform.position;
            Vector3 fromPlayer = transform.position - playerPos;
            float speed = BaseCruiseSpeed * cruiseSpeedMultiplier;
            float turnSharpness = CruiseTurnSharpness;
            bool playerNearFish = fromPlayer.sqrMagnitude < FleeDistance * FleeDistance;

            if (playerNearFish && school != null)
                school.ReportThreat(fromPlayer);

            if (school != null)
                school.Update();

            bool flee = playerNearFish || (school != null && school.IsDisrupted);
            if (flee)
            {
                fromPlayer.y *= 0.25f;
                Vector3 away = school != null ? school.GetFleeDirection(transform.position, playerPos) : fromPlayer;
                if (away.sqrMagnitude > 0.01f)
                {
                    away.Normalize();
                    RefreshFleeDart(away);
                    targetDirection = fleeDartDirection.sqrMagnitude > 0.01f ? fleeDartDirection : away;
                }

                speed = BaseFleeSpeed * fleeSpeedMultiplier;
                turnSharpness = FleeTurnSharpness;
                nextTurnTime = Time.time + 1f;
            }
            else
            {
                SwimWithSchool();
                if (school != null)
                    turnSharpness = SchoolCruiseTurnSharpness;
            }

            if (targetDirection.sqrMagnitude > 0.01f)
            {
                swimDirection = Vector3.Slerp(swimDirection, targetDirection, Time.deltaTime * turnSharpness);
                if (swimDirection.sqrMagnitude > 0.01f)
                    swimDirection.Normalize();
                else
                    swimDirection = targetDirection;
            }

            transform.position += swimDirection * speed * Time.deltaTime;
            ClampToWater();
            DeepWaterRendering.FaceMainCamera(transform);
        }

        private void PickWanderDirection()
        {
            targetDirection = Random.insideUnitSphere;
            targetDirection.y *= 0.25f;
            if (targetDirection.sqrMagnitude < 0.01f)
                targetDirection = Vector3.forward;

            targetDirection.Normalize();
            if (swimDirection.sqrMagnitude < 0.01f)
                swimDirection = targetDirection;
            nextTurnTime = Time.time + Random.Range(TurnIntervalMin, TurnIntervalMax);
        }

        private void SwimWithSchool()
        {
            if (school != null)
            {
                Vector3 toHome = school.Center + schoolOffset - transform.position;
                Vector3 toCenter = school.Center - transform.position;
                toHome.y *= 0.25f;
                toCenter.y *= 0.25f;
                Vector3 schoolDirection = school.CurrentDirection;
                schoolDirection.y *= 0.5f;
                if (schoolDirection.sqrMagnitude < 0.01f)
                    schoolDirection = swimDirection.sqrMagnitude > 0.01f ? swimDirection : Vector3.forward;

                schoolDirection.Normalize();

                if (toCenter.sqrMagnitude > school.Radius * school.Radius)
                {
                    Vector3 returnDirection = schoolDirection + toHome.normalized * SchoolReturnCohesionWeight;
                    targetDirection = returnDirection.sqrMagnitude > 0.01f ? returnDirection.normalized : schoolDirection;
                    return;
                }

                Vector3 cohesion = toHome / Mathf.Max(0.1f, school.Radius);
                Vector3 direction = schoolDirection + cohesion * SchoolCohesionWeight;
                targetDirection = direction.sqrMagnitude > 0.01f ? direction.normalized : schoolDirection;
                return;
            }

            if (Time.time >= nextTurnTime)
                PickWanderDirection();
        }

        private void RefreshFleeDart(Vector3 away)
        {
            if (Time.time < nextFleeDartTime && fleeDartDirection.sqrMagnitude > 0.01f)
                return;

            Vector3 lateral = Vector3.Cross(Vector3.up, away);
            if (lateral.sqrMagnitude < 0.01f)
                lateral = Vector3.right;

            lateral.Normalize();
            if (Random.value < 0.5f)
                lateral = -lateral;

            float angle = Random.Range(FleeDartAngleMin, FleeDartAngleMax) * Mathf.Deg2Rad;
            Vector3 direction = away * Mathf.Cos(angle) + lateral * Mathf.Sin(angle);
            direction.y += Random.Range(-FleeDartVerticalBias, FleeDartVerticalBias);

            if (direction.sqrMagnitude < 0.01f)
                direction = away;

            fleeDartDirection = direction.normalized;
            nextFleeDartTime = Time.time + Random.Range(fleeDartHoldMin, fleeDartHoldMax);
        }

        private void ClampToWater()
        {
            DeepWaterColumn column;
            if (!TryGetCurrentWaterColumn(out column) ||
                column.Depth < 2f)
            {
                if (hasLastSafePosition)
                    transform.position = lastSafePosition;

                swimDirection = -swimDirection;
                return;
            }

            Vector3 pos = transform.position;
            float minY = column.SeafloorWorldY + SeafloorClearance;
            float maxY = column.OceanWorldY - SurfaceClearance;

            if (pos.y < minY)
            {
                pos.y = minY;
                swimDirection.y = Mathf.Abs(swimDirection.y);
            }
            else if (pos.y > maxY)
            {
                pos.y = maxY;
                swimDirection.y = -Mathf.Abs(swimDirection.y);
            }

            transform.position = pos;
            lastSafePosition = pos;
            hasLastSafePosition = true;

            if (column.Parent != null && transform.parent != column.Parent)
                transform.parent = column.Parent;
        }

        private bool TryGetCurrentWaterColumn(out DeepWaterColumn column)
        {
            if (hasCachedColumn && Time.time < nextWaterColumnRefreshTime)
            {
                column = cachedColumn;
                return true;
            }

            nextWaterColumnRefreshTime = Time.time + WaterColumnRefreshInterval;
            hasCachedColumn = DeepWaterWorld.TryGetWaterColumn(transform.position.x, transform.position.z, out cachedColumn);
            column = cachedColumn;
            return hasCachedColumn;
        }

    }
}

