using System.Collections;
using UnityEngine;
using UnityEngine.AI;

[AddComponentMenu("FPS/Enemy Spawner")]
public class EnemySpawner : MonoBehaviour
{
    [Header("Prefab")]
    [SerializeField] private GameObject enemyPrefab;

    [Header("Area Settings")]
    [Tooltip("If set, the BoxCollider defines the spawn area (local space). Otherwise use Center/Size.")]
    [SerializeField] private BoxCollider areaBox;
    [SerializeField] private Vector3 areaCenter = Vector3.zero;
    [SerializeField] private Vector3 areaSize = new Vector3(10f, 1f, 10f);

    [Header("Spawn Timing")]
    [SerializeField] private float spawnIntervalMin = 1.0f;
    [SerializeField] private float spawnIntervalMax = 3.0f;

    [Header("Collision Check")]
    [SerializeField] private LayerMask obstacleMask = ~0;
    [Tooltip("Vertical height of the capsule used to check for obstacles before spawning")]
    [SerializeField] private float checkCapsuleHeight = 1.8f;
    [Tooltip("Radius of the capsule used to check for obstacles before spawning")]
    [SerializeField] private float checkRadius = 0.4f;
    [SerializeField] private int maxSpawnAttempts = 8;

    [Header("Options")]
    [SerializeField] private bool startOnAwake = true;
    [Tooltip("Height offset applied when actually instantiating the prefab (helps avoid spawning intersecting the floor)")]
    [SerializeField] private float spawnHeightOffset = 0.5f;
    [Header("Limits")]
    [Tooltip("Maximum number of active spawned enemies from this spawner at the same time (0 = unlimited)")]
    [SerializeField] private int maxActiveSpawned = 0;

    // runtime
    private int currentActiveSpawned = 0;
    [Header("Patrol")]
    [Tooltip("Optional PatrolPath to assign to spawned enemies. If null, spawned enemies can wander inside the spawn area instead.")]
    [SerializeField] private ScriptableObject patrolPath;
    [Tooltip("Delay (seconds) after spawning before the enemy starts patrolling/wandering")]
    [SerializeField] private float patrolStartDelay = 2f;
    [Tooltip("If no PatrolPath is assigned, enable simple wandering within the spawn area")]
    [SerializeField] private bool enableWanderIfNoPath = true;
    [SerializeField] private float wanderIntervalMin = 2f;
    [SerializeField] private float wanderIntervalMax = 5f;

    private Coroutine spawnRoutine;

    private void Awake()
    {
        if (startOnAwake)
            StartSpawning();
    }

    private IEnumerator StartPatrolOrWander(GameObject inst)
    {
        if (inst == null)
            yield break;

        // stop agent movement until patrol/wander starts
        var agent = inst.GetComponent<NavMeshAgent>();
        if (agent != null)
            agent.isStopped = true;

        // assign patrol path if available (via reflection to avoid circular dependency)
        var enemyCtrl = inst.GetComponent("EnemyController");
        if (enemyCtrl != null && patrolPath != null)
        {
            var type = enemyCtrl.GetType();
            var patrolPathProp = type.GetProperty("PatrolPath");
            if (patrolPathProp != null)
            {
                patrolPathProp.SetValue(enemyCtrl, patrolPath);
            }

            var resetMethod = type.GetMethod("ResetPathDestination");
            if (resetMethod != null)
            {
                resetMethod.Invoke(enemyCtrl, null);
            }

            var setNodeMethod = type.GetMethod("SetPathDestinationToClosestNode");
            if (setNodeMethod != null)
            {
                setNodeMethod.Invoke(enemyCtrl, null);
            }
        }

        // wait before starting
        yield return new WaitForSeconds(Mathf.Max(0f, patrolStartDelay));

        if (agent != null)
            agent.isStopped = false;

        // if no patrol path assigned, optionally start simple wandering inside area
        bool hasNoPatrolPath = true;
        if (enemyCtrl != null)
        {
            var type = enemyCtrl.GetType();
            var patrolPathProp = type.GetProperty("PatrolPath");
            if (patrolPathProp != null)
            {
                hasNoPatrolPath = patrolPathProp.GetValue(enemyCtrl) == null;
            }
        }

        if (enemyCtrl != null && hasNoPatrolPath && enableWanderIfNoPath)
        {
            while (inst != null)
            {
                Vector3 rnd = GetRandomPointInArea();
                NavMeshHit hit;
                if (NavMesh.SamplePosition(rnd, out hit, 2f, NavMesh.AllAreas))
                {
                    if (agent != null)
                        agent.SetDestination(hit.position);
                }

                float wait = Random.Range(wanderIntervalMin, wanderIntervalMax);
                yield return new WaitForSeconds(wait);
            }
        }
    }

    public void StartSpawning()
    {
        if (enemyPrefab == null)
        {
            Debug.LogWarning("EnemySpawner: enemyPrefab is not assigned.");
            return;
        }

        if (spawnRoutine == null)
            spawnRoutine = StartCoroutine(SpawnLoop());
    }

    public void StopSpawning()
    {
        if (spawnRoutine != null)
        {
            StopCoroutine(spawnRoutine);
            spawnRoutine = null;
        }
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            float wait = Random.Range(spawnIntervalMin, spawnIntervalMax);
            yield return new WaitForSeconds(wait);

            bool spawned = false;
            for (int i = 0; i < maxSpawnAttempts; i++)
            {
                Vector3 pos = GetRandomPointInArea();
                if (maxActiveSpawned > 0 && currentActiveSpawned >= maxActiveSpawned)
                {
                    // reached limit for this area
                    Debug.Log($"EnemySpawner: reached maxActiveSpawned ({maxActiveSpawned}), skipping spawn this cycle.");
                    break;
                }

                if (IsPositionClear(pos))
                {
                    Vector3 spawnPos = pos + Vector3.up * spawnHeightOffset;
                    var inst = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);
                    // attach tracker to decrement the counter when destroyed
                    var tracker = inst.AddComponent<SpawnedEnemyTracker>();
                    tracker.Spawner = this;
                    currentActiveSpawned++;
                    Debug.Log($"EnemySpawner: Spawned '{enemyPrefab.name}' at {spawnPos.ToString("F2")}. Active: {currentActiveSpawned}/{(maxActiveSpawned>0?maxActiveSpawned:int.MaxValue)}", inst);
                    // initialize patrol or wandering behavior
                    StartCoroutine(StartPatrolOrWander(inst));
                    spawned = true;
                    break;
                }
                else
                {
                    Debug.Log($"EnemySpawner: spawn attempt {i} blocked at {pos.ToString("F2")}");
                }
            }

            if (!spawned)
            {
                // Couldn't find a free spot this cycle. Skip until next interval.
            }
        }
    }

    private Vector3 GetRandomPointInArea()
    {
        if (areaBox != null)
        {
            // Use BoxCollider bounds (local to world)
            Bounds b = areaBox.bounds;
            float x = Random.Range(b.min.x, b.max.x);
            float y = Random.Range(b.min.y, b.max.y);
            float z = Random.Range(b.min.z, b.max.z);
            return new Vector3(x, y, z);
        }
        else
        {
            Vector3 half = areaSize * 0.5f;
            Vector3 local = new Vector3(Random.Range(-half.x, half.x), Random.Range(-half.y, half.y), Random.Range(-half.z, half.z));
            return transform.TransformPoint(areaCenter + local);
        }
    }

    private bool IsPositionClear(Vector3 position)
    {
        // Apply spawn height offset before checking so we test the actual spawn area
        Vector3 pos = position + Vector3.up * spawnHeightOffset;

        // Build a vertical capsule centered at pos
        float halfHeight = Mathf.Max(0.01f, (checkCapsuleHeight * 0.5f) - checkRadius);
        Vector3 pointBottom = pos + Vector3.up * checkRadius;
        Vector3 pointTop = pos + Vector3.up * (2f * halfHeight + checkRadius);

        // Use CheckCapsule to detect colliders in obstacleMask, ignore trigger colliders
        bool blocked = Physics.CheckCapsule(pointBottom, pointTop, checkRadius, obstacleMask, QueryTriggerInteraction.Ignore);
        return !blocked;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        if (areaBox != null)
        {
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawCube(areaBox.bounds.center, areaBox.bounds.size);
            Gizmos.color = new Color(0f, 1f, 0f, 0.6f);
            Gizmos.DrawWireCube(areaBox.bounds.center, areaBox.bounds.size);
        }
        else
        {
            Matrix4x4 tm = transform.localToWorldMatrix;
            Gizmos.matrix = tm;
            Gizmos.DrawCube(areaCenter, areaSize);
            Gizmos.color = new Color(0f, 1f, 0f, 0.6f);
            Gizmos.DrawWireCube(areaCenter, areaSize);
        }

        // Draw sample capsule at transform position for radius/height visualization
        Gizmos.color = Color.yellow;
        Vector3 sample = transform.position;
        Vector3 bottom = sample + Vector3.up * checkRadius;
        float halfHeight = Mathf.Max(0.01f, (checkCapsuleHeight * 0.5f) - checkRadius);
        Vector3 top = sample + Vector3.up * (2f * halfHeight + checkRadius);
        DrawCapsuleGizmo(bottom, top, checkRadius);
    }

    private void DrawCapsuleGizmo(Vector3 p0, Vector3 p1, float radius)
    {
        // Approximate capsule with spheres and a line for visualization
        Gizmos.DrawWireSphere(p0, radius);
        Gizmos.DrawWireSphere(p1, radius);
        Gizmos.DrawLine(p0 + Vector3.forward * radius, p1 + Vector3.forward * radius);
        Gizmos.DrawLine(p0 - Vector3.forward * radius, p1 - Vector3.forward * radius);
        Gizmos.DrawLine(p0 + Vector3.right * radius, p1 + Vector3.right * radius);
        Gizmos.DrawLine(p0 - Vector3.right * radius, p1 - Vector3.right * radius);
    }

    // Public helper for immediate spawn attempt at a random valid position
    [ContextMenu("Spawn Now")]
    public void SpawnNow()
    {
        if (enemyPrefab == null) return;
        for (int i = 0; i < maxSpawnAttempts; i++)
        {
            Vector3 pos = GetRandomPointInArea();
            if (IsPositionClear(pos))
            {
                if (maxActiveSpawned > 0 && currentActiveSpawned >= maxActiveSpawned)
                {
                    Debug.Log($"EnemySpawner: reached maxActiveSpawned ({maxActiveSpawned}), skipping SpawnNow.");
                    return;
                }

                Vector3 spawnPos = pos + Vector3.up * spawnHeightOffset;
                var inst = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);
                var tracker = inst.AddComponent<SpawnedEnemyTracker>();
                tracker.Spawner = this;
                currentActiveSpawned++;
                Debug.Log($"EnemySpawner: Spawned '{enemyPrefab.name}' at {spawnPos.ToString("F2")}. Active: {currentActiveSpawned}/{(maxActiveSpawned>0?maxActiveSpawned:int.MaxValue)}", inst);
                StartCoroutine(StartPatrolOrWander(inst));
                return;
            }
            else
            {
                Debug.Log($"EnemySpawner: Spawn attempt {i} blocked at {pos.ToString("F2")}");
            }
        }
        Debug.Log("EnemySpawner: no free spawn position found.");
    }

    // Called by SpawnedEnemyTracker when a spawned object is destroyed
    public void NotifySpawnedDestroyed()
    {
        currentActiveSpawned = Mathf.Max(0, currentActiveSpawned - 1);
        Debug.Log($"EnemySpawner: spawned destroyed. Active now: {currentActiveSpawned}");
    }
}
