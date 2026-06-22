// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
    internal class PassiveFishBehaviour : MonoBehaviour
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

        // Horizontal collision check: probes forward each frame so fish bounce
        // off seafloor walls, shore cliffs, and vanilla terrain instead of
        // passing through them. The seafloor mesh sits on the Ignore Raycast
        // layer (so the shore-exit assist's downward ray hits vanilla terrain
        // first), so we have to opt that layer back in with a wider mask.
        private const int ObstacleProbeFrameInterval = 5;
        private const float ObstacleProbePlayerDistance = 60f;
        private const float ObstacleProbePlayerDistanceSqr = ObstacleProbePlayerDistance * ObstacleProbePlayerDistance;
        private const float CollisionProbeDistance = 0.6f;
        private const float CollisionProbeMargin = 0.15f;
        private static int collisionLayerMask = -1;

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
		private Renderer visibilityRenderer;
        private DeepWaterColumn cachedColumn;
        private float nextWaterColumnRefreshTime;
        private bool hasCachedColumn;
        private int obstacleProbeFrameOffset;

        internal void Initialize(DaggerfallLoot lootTarget, float cruiseMultiplier, float fleeMultiplier, PassiveFishSchool fishSchool, float dartHoldMin, float dartHoldMax)
        {
            loot = lootTarget;
            cruiseSpeedMultiplier = cruiseMultiplier;
            fleeSpeedMultiplier = fleeMultiplier;
            school = fishSchool;
            fleeDartHoldMin = Mathf.Max(0.1f, dartHoldMin);
            fleeDartHoldMax = Mathf.Max(fleeDartHoldMin, dartHoldMax);
			visibilityRenderer = GetComponent<Renderer>();
            if (school != null)
                schoolOffset = transform.position - school.Center;

            lastSafePosition = transform.position;
            hasLastSafePosition = true;
            obstacleProbeFrameOffset = Random.Range(0, ObstacleProbeFrameInterval);
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
			UpdateAboveSurfaceVisibility(gameManager, playerPos);
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

            Vector3 desiredStep = swimDirection * speed * Time.deltaTime;
            if (!TryAvoidObstacle(desiredStep, playerPos))
                transform.position += desiredStep;

            ClampToWater();
            DeepWaterRendering.FaceMainCamera(transform);
        }

		private void UpdateAboveSurfaceVisibility(GameManager gameManager, Vector3 playerPos)
		{
			if (visibilityRenderer == null)
				visibilityRenderer = GetComponent<Renderer>();
			if (visibilityRenderer == null || gameManager.MainCamera == null)
				return;

			float oceanY;
			if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY) ||
				gameManager.MainCamera.transform.position.y < oceanY - 0.05f)
			{
				visibilityRenderer.enabled = true;
				return;
			}

			Vector3 flatDelta = transform.position - playerPos;
			flatDelta.y = 0f;
			float visibleDistance = WaterSurfaceResources.GetTopSurfaceOpaqueFadeEnd();
			visibilityRenderer.enabled = flatDelta.sqrMagnitude <= visibleDistance * visibleDistance;
		}

        // Probe forward in the swim direction. If we'd hit anything solid, hold
        // position this frame and reflect the swim/target direction off the
        // surface normal so subsequent frames steer the fish along the wall
        // rather than into it. Returns true when an obstacle was found (so the
        // caller skips the position update).
        private bool TryAvoidObstacle(Vector3 desiredStep, Vector3 playerPos)
        {
            if (desiredStep.sqrMagnitude < 1e-6f)
                return false;

            if ((transform.position - playerPos).sqrMagnitude > ObstacleProbePlayerDistanceSqr)
                return false;

            if ((Time.frameCount + obstacleProbeFrameOffset) % ObstacleProbeFrameInterval != 0)
                return false;

            if (collisionLayerMask < 0)
            {
                int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
                collisionLayerMask = Physics.DefaultRaycastLayers;
                if (ignoreRaycast >= 0)
                    collisionLayerMask |= (1 << ignoreRaycast);
            }

            float distance = Mathf.Max(
                CollisionProbeDistance,
                desiredStep.magnitude * ObstacleProbeFrameInterval + CollisionProbeMargin);
            RaycastHit hit;
            if (!Physics.Raycast(transform.position, swimDirection, out hit, distance,
                                 collisionLayerMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            // Reflect direction off the obstacle. Lateral component dominates
            // so fish slide along walls instead of bouncing straight back.
            Vector3 reflected = Vector3.Reflect(swimDirection, hit.normal);
            if (reflected.sqrMagnitude < 0.01f)
                reflected = -swimDirection;
            swimDirection = reflected.normalized;
            targetDirection = swimDirection;
            // Force a fresh wander on next think so the fish doesn't immediately
            // turn back toward the same wall.
            nextTurnTime = Time.time + 0.5f;
            return true;
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
            float seafloorWorldY;
            if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, pos.x, pos.z, out seafloorWorldY))
                return;

            float minY = seafloorWorldY + SeafloorClearance;
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

		internal PassiveFishSchool(Vector3 startCenter, float schoolRadius, float cruiseMultiplier, float fleeMultiplier)
		{
			center = startCenter;
			radius = Mathf.Max(0.1f, schoolRadius);
			cruiseSpeedMultiplier = cruiseMultiplier;
			fleeSpeedMultiplier = fleeMultiplier;
			PickCruiseDirection();
			disruptedDirection = cruiseDirection;
		}

		internal Vector3 Center
		{
			get { return center; }
		}

		internal float Radius
		{
			get { return radius; }
		}

		internal Vector3 CurrentDirection
		{
			get { return IsDisrupted ? disruptedDirection : cruiseDirection; }
		}

		internal bool IsDisrupted
		{
			get { return Time.time < disruptedUntil; }
		}

		internal void ReportThreat(Vector3 awayFromPlayer)
		{
			awayFromPlayer.y *= 0.25f;
			if (awayFromPlayer.sqrMagnitude < 0.01f)
				return;

			disruptedDirection = awayFromPlayer.normalized;
			disruptedUntil = Time.time + DisruptedMemorySeconds;
		}

		internal Vector3 GetFleeDirection(Vector3 fishPosition, Vector3 playerPosition)
		{
			Vector3 away = fishPosition - playerPosition;
			away.y *= 0.25f;
			if (away.sqrMagnitude > 0.01f)
				return away.normalized;

			return disruptedDirection.sqrMagnitude > 0.01f ? disruptedDirection : cruiseDirection;
		}

		internal void Update()
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

			float seafloorWorldY;
			if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, nextCenter.x, nextCenter.z, out seafloorWorldY))
				return;

			float minY = seafloorWorldY + SeafloorClearance;
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
