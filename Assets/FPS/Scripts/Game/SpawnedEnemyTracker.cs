using UnityEngine;

public class SpawnedEnemyTracker : MonoBehaviour
{
    public EnemySpawner Spawner { get; set; }

    private void OnDestroy()
    {
        if (Spawner != null)
        {
            Spawner.NotifySpawnedDestroyed();
        }
    }
}
